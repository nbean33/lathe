using System.Globalization;
using Lathe.Core;
using Lathe.Core.Project;

namespace Lathe.Cam;

public sealed class PostOptions
{
    public UnitSystem Units { get; init; } = UnitSystem.Mm;
    /// <summary>G95 (mm/rev) when the spindle has an encoder, else G94 (mm/min).</summary>
    public bool FeedPerRev { get; init; } = true;
    public int SpinUpSeconds { get; init; } = 2;
}

public sealed class PostResult
{
    public List<string> Lines { get; } = new();
    /// <summary>For each line: index into CamResult.Ops, or -1 for header/footer.</summary>
    public List<int> LineOp { get; } = new();
    /// <summary>Modal setup line, re-sent first when starting from an operation.</summary>
    public string Preamble { get; set; } = "";
    public double XMin { get; set; } = double.MaxValue;
    public double XMax { get; set; } = double.MinValue;
    public double ZMin { get; set; } = double.MaxValue;
    public double ZMax { get; set; } = double.MinValue;
    public int MaxRpm { get; set; }
    public double MaxFeedMmMin { get; set; }
    public bool UsesThreading { get; set; }
    public UnitSystem Units { get; init; }
    public string Text => string.Join(Environment.NewLine, Lines) + Environment.NewLine;
}

/// <summary>
/// grblHAL lathe-mode post. X words are diameters (G7). Tool changes are the app directive (LS:TOOL n);
/// each operation starts the spindle itself and stops it at the end, so any operation can be restarted on its own.
/// </summary>
public static class GrblHalPost
{
    public static PostResult Build(CamResult cam, LatheProject project, MachineProfile machine, PostOptions? o = null)
    {
        o ??= new PostOptions { Units = project.Units, FeedPerRev = cam.SpindleSync, SpinUpSeconds = machine.SpindleSpinUpSeconds };
        var res = new PostResult { Units = o.Units };
        bool inch = o.Units == UnitSystem.Inch;
        int dec = inch ? 4 : 3;
        string N(double mm) { var v = inch ? mm / 25.4 : mm; v = Math.Round(v, dec); if (v == 0) v = 0; return v.ToString("0." + new string('#', dec), CultureInfo.InvariantCulture); }
        string Fmt(double v, int d) => Math.Round(v, d).ToString("0." + new string('#', d), CultureInfo.InvariantCulture);
        int curOp = -1;
        void L(string s) { res.Lines.Add(s); res.LineOp.Add(curOp); }

        var st = project.Stock;
        L($"(LatheStudio: {Clean(project.Name)})");
        L($"(Material {Clean(project.Material)}; stock D{N(st.Diameter)} x {N(st.StickOut)} out of the jaws)");
        L($"(X = diameter. Z0 = finished face. Units {(inch ? "inch" : "mm")}.)");
        res.Preamble = $"G7 G18 G90 G54 {(inch ? "G20" : "G21")} {(o.FeedPerRev ? "G95" : "G94")}";
        L(res.Preamble);

        for (int k = 0; k < cam.Ops.Count; k++)
        {
            var op = cam.Ops[k];
            curOp = k;
            L($"(LS:OP {k} {Clean(op.Name)})");
            L($"({Clean(op.Name)} - T{op.Tool.Number} {Clean(op.Tool.Name)}, {op.Rpm} rpm)");
            L($"(LS:TOOL {op.Tool.Number})");
            L($"G0 X{N(2 * op.Start.R)}");
            L($"G0 Z{N(op.Start.Z)}");
            L($"S{op.Rpm} M3");
            if (op.Coolant) L("M8");
            L($"G4 P{Math.Max(0, o.SpinUpSeconds)}");
            res.MaxRpm = Math.Max(res.MaxRpm, op.Rpm);
            Track(res, op.Start);
            double lastF = -1;
            foreach (var m in op.Moves)
            {
                Track(res, m.To);
                switch (m.Kind)
                {
                    case MoveKind.Rapid:
                        L($"G0 X{N(2 * m.To.R)} Z{N(m.To.Z)}");
                        break;
                    case MoveKind.Feed:
                    {
                        double perMin = m.Feed * op.Rpm;
                        res.MaxFeedMmMin = Math.Max(res.MaxFeedMmMin, perMin);
                        double f = o.FeedPerRev ? m.Feed : perMin;
                        if (inch) f /= 25.4;
                        string fs = Fmt(f, o.FeedPerRev ? (inch ? 5 : 4) : (inch ? 2 : 1));
                        string fw = Math.Abs(f - lastF) > 1e-9 ? $" F{fs}" : "";
                        lastF = f;
                        L($"G1 X{N(2 * m.To.R)} Z{N(m.To.Z)}{fw}");
                        break;
                    }
                    case MoveKind.Thread:
                        res.UsesThreading = true;
                        res.MaxFeedMmMin = Math.Max(res.MaxFeedMmMin, m.Pitch * op.Rpm);
                        L($"G33 X{N(2 * m.To.R)} Z{N(m.To.Z)} K{Fmt(inch ? m.Pitch / 25.4 : m.Pitch, inch ? 6 : 4)}");
                        lastF = -1;
                        break;
                }
            }
            if (op.Coolant) L("M9");
            L("M5");
        }
        curOp = -1;
        L("(end of program)");
        L("M5");
        L("M9");
        L("M30");
        return res;
    }

    private static void Track(PostResult r, Core.Geometry.Pt p)
    {
        r.XMin = Math.Min(r.XMin, 2 * p.R); r.XMax = Math.Max(r.XMax, 2 * p.R);
        r.ZMin = Math.Min(r.ZMin, p.Z); r.ZMax = Math.Max(r.ZMax, p.Z);
    }

    /// <summary>G-code comments can't contain parentheses or semicolons.</summary>
    private static string Clean(string s) => new string((s ?? "").Where(c => c is not ('(' or ')' or ';') && !char.IsControl(c)).ToArray()).Trim();
}

/// <summary>State of the connected machine, for preflight. X values are diameters (mm).</summary>
public sealed record MachineFacts(
    bool Connected, bool Idle, bool Homed, bool LatheMode, bool SpindleSync, bool SoftLimits,
    double WcoX, double WcoZ, double? TravelX, double? TravelZ, bool WorkZeroConfirmed, bool UnitsMm = true);

/// <summary>Checks that must pass before Cycle Start is allowed.</summary>
public static class Preflight
{
    public static List<Issue> Check(CamResult cam, PostResult post, MachineProfile m, MachineFacts? f)
    {
        var list = new List<Issue>();
        if (cam.AllIssues.HasErrors()) list.Add(Issue.Err("The program has errors; fix them before running."));
        if (cam.Sim is null) list.Add(Issue.Err("The program has not been simulated."));
        if (post.MaxRpm > m.EffectiveMaxRpm) list.Add(Issue.Err($"Program spins to {post.MaxRpm} rpm; limit is {m.EffectiveMaxRpm}."));
        if (post.MaxFeedMmMin > Math.Max(m.MaxFeedX, m.MaxFeedZ) + 1e-6) list.Add(Issue.Err($"Program feeds up to {Num.F(post.MaxFeedMmMin, 0)} mm/min; machine max is {Num.F(Math.Max(m.MaxFeedX, m.MaxFeedZ), 0)}."));
        if (post.XMin < -2.0) list.Add(Issue.Err($"Program goes to X{Num.F(post.XMin)} (well past center)."));
        if (f is null || !f.Connected) { list.Add(Issue.Err("Not connected to the machine.")); return list; }
        if (!f.Idle) list.Add(Issue.Err("Machine is not Idle."));
        if (!f.LatheMode) list.Add(Issue.Err("Controller is not in lathe mode ($32=2); X would be treated as radius/mill axis."));
        if (post.UsesThreading && !f.SpindleSync) list.Add(Issue.Err("Program threads but the controller reports no spindle sync."));
        if (!f.Homed) list.Add(Issue.Warn("Machine is not homed: soft limits and the travel check can't protect you."));
        if (!f.SoftLimits) list.Add(Issue.Warn("Soft limits are off ($20=0)."));
        if (!f.WorkZeroConfirmed) list.Add(Issue.Err("Work zero has not been set/confirmed for this setup (touch off X and Z)."));
        if (f.Homed && f.TravelX is double tx && f.TravelZ is double tz)
        {
            // grbl machine space after homing: [-travel, 0]. X is reported as a diameter in lathe mode.
            double xmLo = post.XMin + f.WcoX, xmHi = post.XMax + f.WcoX, zmLo = post.ZMin + f.WcoZ, zmHi = post.ZMax + f.WcoZ;
            if (xmHi > 0.01 || xmLo < -2 * tx - 0.01) list.Add(Issue.Err($"X moves (machine Ø{Num.F(xmLo)}…Ø{Num.F(xmHi)}) leave the travel range. Check work zero."));
            if (zmHi > 0.01 || zmLo < -tz - 0.01) list.Add(Issue.Err($"Z moves (machine Z{Num.F(zmLo)}…Z{Num.F(zmHi)}) leave the travel range. Check work zero and stick-out."));
        }
        return list;
    }
}
