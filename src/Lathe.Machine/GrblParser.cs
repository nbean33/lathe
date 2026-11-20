using System.Globalization;

namespace Lathe.Machine;

public enum MachineState { Unknown, Idle, Run, Hold, Jog, Alarm, Door, Check, Home, Sleep, Tool }

/// <summary>One parsed real-time status report. X/Z are exactly as the controller reports them
/// (grblHAL reports X as a diameter while G7 is active).</summary>
public sealed record GrblStatus(MachineState State, int SubState, double MX, double MZ, double WX, double WZ,
    double Feed, double SpindleRpm, int FeedOv, int RapidOv, int SpindleOv, string Pins, string Accessories, bool HasWco);

public static class GrblParser
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    /// <summary>Parses "&lt;Idle|MPos:..|FS:..|WCO:..&gt;". Returns null if the line is not a status report.</summary>
    public static GrblStatus? ParseStatus(string line, (double X, double Z)? lastWco)
    {
        line = line.Trim();
        if (line.Length < 3 || line[0] != '<' || line[^1] != '>') return null;
        var fields = line[1..^1].Split('|');

        var stateField = fields[0];
        int sub = 0;
        int colon = stateField.IndexOf(':');
        var stateName = colon >= 0 ? stateField[..colon] : stateField;
        if (colon >= 0) int.TryParse(stateField[(colon + 1)..], NumberStyles.Integer, CI, out sub);
        var state = stateName switch
        {
            "Idle" => MachineState.Idle,
            "Run" => MachineState.Run,
            "Hold" => MachineState.Hold,
            "Jog" => MachineState.Jog,
            "Alarm" => MachineState.Alarm,
            "Door" => MachineState.Door,
            "Check" => MachineState.Check,
            "Home" => MachineState.Home,
            "Sleep" => MachineState.Sleep,
            "Tool" => MachineState.Tool,
            _ => MachineState.Unknown,
        };

        double[]? m = null, w = null, wco = null;
        double feed = 0, rpm = 0;
        int fo = 100, ro = 100, so = 100;
        string pins = "", acc = "";
        for (int i = 1; i < fields.Length; i++)
        {
            var f = fields[i];
            int c = f.IndexOf(':');
            if (c < 0) continue;
            var key = f[..c];
            var val = f[(c + 1)..];
            switch (key)
            {
                case "MPos": m = Nums(val); if (m == null) return null; break;
                case "WPos": w = Nums(val); if (w == null) return null; break;
                case "WCO": wco = Nums(val); break;
                case "FS":
                    { var a = Nums(val); if (a is { Length: > 0 }) { feed = a[0]; rpm = a.Length > 1 ? a[1] : 0; } break; }
                case "F":
                    { var a = Nums(val); if (a is { Length: > 0 }) feed = a[0]; break; }
                case "Ov":
                    {
                        var a = Nums(val);
                        if (a is { Length: >= 3 }) { fo = (int)a[0]; ro = (int)a[1]; so = (int)a[2]; }
                        break;
                    }
                case "Pn": pins = val; break;
                case "A": acc = val; break;
            }
        }

        var off = wco != null ? XZ(wco) : lastWco ?? (0, 0);
        double mx, mz, wx, wz;
        if (m != null)
        {
            (mx, mz) = XZ(m);
            wx = mx - off.X; wz = mz - off.Z;
        }
        else if (w != null)
        {
            (wx, wz) = XZ(w);
            mx = wx + off.X; mz = wz + off.Z;
        }
        else return null;

        return new GrblStatus(state, sub, mx, mz, wx, wz, feed, rpm, fo, ro, so, pins, acc, wco != null);
    }

    /// <summary>Parses a "$32=2" settings line.</summary>
    public static bool TryParseSetting(string line, out int id, out string value)
    {
        id = 0; value = "";
        line = line.Trim();
        if (line.Length < 3 || line[0] != '$') return false;
        int eq = line.IndexOf('=');
        if (eq < 2) return false;
        if (!int.TryParse(line.AsSpan(1, eq - 1), NumberStyles.None, CI, out id)) return false;
        value = line[(eq + 1)..].Trim();
        return true;
    }

    // 2 values = X,Z (2-axis lathe build); 3+ values = X,Y,Z.
    static (double X, double Z) XZ(double[] a) => a.Length switch
    {
        0 => (0, 0),
        1 => (a[0], 0),
        2 => (a[0], a[1]),
        _ => (a[0], a[2]),
    };

    static double[]? Nums(string s)
    {
        var parts = s.Split(',');
        var r = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CI, out r[i])) return null;
        return r;
    }
}
