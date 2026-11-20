using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lathe.Machine;

public sealed class VirtualMachineOptions
{
    /// <summary>X travel in mm of radius (machine X range is [-TravelXmm, 0]).</summary>
    public double TravelXmm = 150;
    public double TravelZmm = 400;
    /// <summary>Start in ALARM (homing required) like a grblHAL build with homing init lock.</summary>
    public bool HomingRequired = true;
    public bool SpindleSync = true;
    public int RxBufferSize = 1024;
    /// <summary>&gt;1 = faster than real time; double.PositiveInfinity = instant moves.</summary>
    public double TimeScale = 1.0;
    public int MaxRpm = 3000;
}

/// <summary>
/// In-process grblHAL lathe simulator speaking the same serial protocol. Used for demos and tests.
/// Internally X is a radius; while G7 is active X words and reported X positions are diameters.
/// Arc I offsets are radius values (not doubled in G7).
/// </summary>
public sealed class VirtualGrblHal : IMachineTransport
{
    public const string WelcomeText = "GrblHAL 1.1f ['$' or '$HELP' for help]";
    const int PlannerSize = 16;
    const double RapidMmMin = 3000;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    enum Kind { Move, Spindle, Coolant, Dwell, Pause, End }

    sealed class Block
    {
        public Kind Kind;
        public double X, Z, Speed, Value;
        public bool Rapid, Jog, On;
        public int Dir;
    }

    sealed class Modal
    {
        public bool Inches, Incremental, FeedPerRev, Css, SpOn, Cool;
        public bool Diameter = true;
        public int Motion;
        public int SpDir = 3;
        public double F, S, CssMax;
        public Modal Clone() => (Modal)MemberwiseClone();
    }

    readonly VirtualMachineOptions _o;
    readonly object _sync = new();
    readonly Queue<string> _rx = new();
    readonly LinkedList<Block> _planner = new();
    readonly List<string> _received = new(), _executed = new();
    readonly SortedDictionary<int, string> _settings;
    BlockingCollection<string>? _out;
    Thread? _simThread, _outThread;
    volatile bool _running;

    int _rxBytes, _maxRxBytes;
    bool _overflow;
    double _mx, _mz, _wcoX, _wcoZ, _px, _pz, _curFeed, _rpm;
    bool _alarm, _homed, _hold, _check, _spindleOn, _coolant, _wcoDirty = true;
    int _spindleDir, _feedOv = 100, _rapidOv = 100, _spOv = 100, _reportCount, _programEnds;
    Modal _m = new();

    public VirtualGrblHal(VirtualMachineOptions? options = null)
    {
        _o = options ?? new VirtualMachineOptions();
        _settings = new SortedDictionary<int, string>
        {
            [0] = "10", [1] = "25", [2] = "0", [3] = "0", [4] = "0", [5] = "0", [6] = "0",
            [10] = "511", [11] = "0.010", [12] = "0.002", [13] = "0",
            [20] = "1", [21] = "0", [22] = "1", [23] = "0", [24] = "25.0", [25] = "500.0", [26] = "250", [27] = "1.000",
            [30] = _o.MaxRpm.ToString(CI), [31] = "0", [32] = "2",
            [100] = "800.000", [102] = "400.000", [110] = "3000.000", [112] = "3000.000",
            [120] = "200.000", [122] = "200.000",
            [130] = _o.TravelXmm.ToString("0.000", CI), [132] = _o.TravelZmm.ToString("0.000", CI),
        };
    }

    // ------------------------------------------------------------------ inspection (tests / UI)

    public MachineState State { get { lock (_sync) return ParseState(StateText()); } }
    public double MX { get { lock (_sync) return _mx; } }
    public double MZ { get { lock (_sync) return _mz; } }
    public double WcoX { get { lock (_sync) return _wcoX; } }
    public double WcoZ { get { lock (_sync) return _wcoZ; } }
    public bool SpindleOn { get { lock (_sync) return _spindleOn; } }
    public double SpindleRpm { get { lock (_sync) return _spindleOn ? _rpm : 0; } }
    public bool Coolant { get { lock (_sync) return _coolant; } }
    public bool CheckMode { get { lock (_sync) return _check; } }
    public bool IsHomed { get { lock (_sync) return _homed; } }
    public int ProgramEndCount { get { lock (_sync) return _programEnds; } }
    /// <summary>Every line the host wrote (including ones later discarded by a reset).</summary>
    public IReadOnlyList<string> ReceivedLines { get { lock (_sync) return [.. _received]; } }
    /// <summary>Lines actually parsed/executed.</summary>
    public IReadOnlyList<string> ExecutedLines { get { lock (_sync) return [.. _executed]; } }
    public int MaxRxBytes { get { lock (_sync) return _maxRxBytes; } }
    public bool RxOverflow { get { lock (_sync) return _overflow; } }

    // ------------------------------------------------------------------ transport

    public bool IsOpen => _running;
    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted { add { } remove { } }

    public void Open()
    {
        lock (_sync)
        {
            if (_running) return;
            _rx.Clear(); _planner.Clear(); _received.Clear(); _executed.Clear();
            _rxBytes = _maxRxBytes = 0; _overflow = false;
            _mx = _mz = _px = _pz = _wcoX = _wcoZ = 0; _curFeed = _rpm = 0;
            _homed = _hold = _check = _spindleOn = _coolant = false;
            _feedOv = _rapidOv = _spOv = 100; _m = new Modal(); _wcoDirty = true;
            _alarm = HomingLock;
            _out = new BlockingCollection<string>();
            _running = true;
            Emit(WelcomeText);
            if (_alarm) Emit("[MSG:'$H'|'$X' to unlock]");
        }
        _outThread = new Thread(OutLoop) { IsBackground = true, Name = "vgrbl-out" };
        _simThread = new Thread(SimLoop) { IsBackground = true, Name = "vgrbl-sim" };
        _outThread.Start();
        _simThread.Start();
    }

    public void Close()
    {
        if (!_running) return;
        _running = false;
        try { _out?.CompleteAdding(); } catch { }
        foreach (var t in new[] { _simThread, _outThread })
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
    }

    public void Dispose() => Close();

    public void WriteLine(string line)
    {
        lock (_sync)
        {
            if (!_running) return;
            _received.Add(line);
            _rxBytes += line.Length + 1;
            _maxRxBytes = Math.Max(_maxRxBytes, _rxBytes);
            if (_rxBytes > _o.RxBufferSize) _overflow = true;
            _rx.Enqueue(line);
        }
    }

    public void WriteRealtime(byte b)
    {
        lock (_sync)
        {
            if (!_running) return;
            switch (b)
            {
                case (byte)'?': Emit(StatusReport()); break;
                case (byte)'!':
                    if (_planner.First?.Value.Jog == true) CancelJog();
                    else if (_planner.Count > 0) _hold = true;
                    break;
                case (byte)'~': if (!_alarm) _hold = false; break;
                case 0x18: Reset(); break;
                case 0x85: CancelJog(); break;
                case 0x90: _feedOv = 100; break;
                case 0x91: _feedOv = Math.Min(200, _feedOv + 10); break;
                case 0x92: _feedOv = Math.Max(10, _feedOv - 10); break;
                case 0x93: _feedOv = Math.Min(200, _feedOv + 1); break;
                case 0x94: _feedOv = Math.Max(10, _feedOv - 1); break;
                case 0x95: _rapidOv = 100; break;
                case 0x96: _rapidOv = 50; break;
                case 0x97: _rapidOv = 25; break;
                case 0x99: _spOv = 100; break;
                case 0x9A: _spOv = Math.Min(200, _spOv + 10); break;
                case 0x9B: _spOv = Math.Max(10, _spOv - 10); break;
                case 0x9C: _spOv = Math.Min(200, _spOv + 1); break;
                case 0x9D: _spOv = Math.Max(10, _spOv - 1); break;
            }
        }
    }

    void Emit(string line)
    {
        try { _out?.Add(line); } catch (InvalidOperationException) { /* closed */ }
    }

    void OutLoop()
    {
        var q = _out!;
        try
        {
            foreach (var l in q.GetConsumingEnumerable())
            {
                if (!_running) break;
                try { LineReceived?.Invoke(l); } catch { /* host bug must not kill the sim */ }
            }
        }
        catch (ObjectDisposedException) { }
    }

    void SimLoop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        while (_running)
        {
            lock (_sync)
            {
                double now = sw.Elapsed.TotalSeconds;
                Advance(now - last);
                last = now;
                ProcessInput();
                Advance(0);
            }
            Thread.Sleep(1);
        }
    }

    // ------------------------------------------------------------------ machine model

    bool HomingLock => _o.HomingRequired && Setting(22) == "1";
    bool SoftLimits => Setting(20) == "1";
    string Setting(int id) => _settings.TryGetValue(id, out var v) ? v.Trim() : "";

    string StateText() =>
        _alarm ? "Alarm" :
        _check ? "Check" :
        _hold ? "Hold:0" :
        _planner.Count > 0 ? (_planner.First!.Value.Jog ? "Jog" : "Run") : "Idle";

    static MachineState ParseState(string s) => s switch
    {
        "Alarm" => MachineState.Alarm,
        "Check" => MachineState.Check,
        "Idle" => MachineState.Idle,
        "Run" => MachineState.Run,
        "Jog" => MachineState.Jog,
        _ => s.StartsWith("Hold", StringComparison.Ordinal) ? MachineState.Hold : MachineState.Unknown,
    };

    string StatusReport()
    {
        _reportCount++;
        double k = _m.Diameter ? 2 : 1;
        var sb = new StringBuilder("<").Append(StateText());
        sb.Append("|MPos:").Append(F3(_mx * k)).Append(",0.000,").Append(F3(_mz));
        sb.Append("|FS:").Append(_curFeed.ToString("0", CI)).Append(',')
          .Append((_spindleOn ? _rpm * _spOv / 100.0 : 0).ToString("0", CI));
        sb.Append("|Ov:").Append(_feedOv).Append(',').Append(_rapidOv).Append(',').Append(_spOv);
        if (_wcoDirty || _reportCount % 10 == 0)
        {
            sb.Append("|WCO:").Append(F3(_wcoX * k)).Append(",0.000,").Append(F3(_wcoZ));
            _wcoDirty = false;
        }
        if (_spindleOn || _coolant)
        {
            sb.Append("|A:");
            if (_spindleOn) sb.Append(_spindleDir == 4 ? 'C' : 'S');
            if (_coolant) sb.Append('F');
        }
        return sb.Append('>').ToString();
    }

    static string F3(double v) => v.ToString("0.000", CI);

    void Advance(double dt)
    {
        double budget = double.IsPositiveInfinity(_o.TimeScale) ? double.PositiveInfinity : dt * _o.TimeScale;
        if (_planner.Count == 0 || _hold || _alarm) { _curFeed = 0; return; }
        while (_planner.First != null && !_hold && !_alarm && budget > 0)
        {
            var b = _planner.First.Value;
            switch (b.Kind)
            {
                case Kind.Move:
                    {
                        double ov = b.Jog ? 100 : b.Rapid ? _rapidOv : _feedOv;
                        double speed = Math.Max(1e-3, b.Speed * ov / 100.0) / 60.0; // mm/s
                        double dist = Math.Sqrt((b.X - _mx) * (b.X - _mx) + (b.Z - _mz) * (b.Z - _mz));
                        _curFeed = speed * 60;
                        double can = speed * budget;
                        if (can >= dist)
                        {
                            _mx = b.X; _mz = b.Z;
                            budget -= dist / speed;
                            _planner.RemoveFirst();
                        }
                        else
                        {
                            double f = can / dist;
                            _mx += (b.X - _mx) * f;
                            _mz += (b.Z - _mz) * f;
                            budget = 0;
                        }
                        break;
                    }
                case Kind.Dwell:
                    if (b.Value <= budget) { budget -= b.Value; _planner.RemoveFirst(); }
                    else { b.Value -= budget; budget = 0; }
                    break;
                case Kind.Spindle:
                    _spindleOn = b.On; _rpm = b.On ? b.Value : 0; _spindleDir = b.Dir;
                    _planner.RemoveFirst();
                    break;
                case Kind.Coolant:
                    _coolant = b.On;
                    _planner.RemoveFirst();
                    break;
                case Kind.Pause:
                    _hold = true;
                    _planner.RemoveFirst();
                    break;
                case Kind.End:
                    _spindleOn = false; _rpm = 0; _coolant = false;
                    _programEnds++;
                    _planner.RemoveFirst();
                    break;
            }
        }
        if (_planner.Count == 0) _curFeed = 0;
    }

    void ProcessInput()
    {
        int n = 0;
        while (_rx.Count > 0 && _planner.Count < PlannerSize && n++ < 64)
        {
            var line = _rx.Dequeue();
            _rxBytes -= line.Length + 1;
            _executed.Add(line);
            var resp = Execute(line);
            if (resp != null) Emit(resp);
        }
    }

    void CancelJog()
    {
        var node = _planner.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value.Jog) _planner.Remove(node);
            node = next;
        }
        _px = _mx; _pz = _mz;
    }

    void Reset()
    {
        bool moving = !_hold && _planner.First?.Value.Kind == Kind.Move;
        _rx.Clear(); _rxBytes = 0; _planner.Clear();
        _hold = false; _spindleOn = false; _rpm = 0; _coolant = false; _curFeed = 0;
        _check = false;
        _feedOv = _rapidOv = _spOv = 100;
        _m = new Modal();
        _px = _mx; _pz = _mz;
        _wcoDirty = true;
        if (moving)
        {
            _alarm = true; _homed = false;
            Emit("ALARM:3");
        }
        Emit(WelcomeText);
        if (!_alarm && HomingLock && !_homed) _alarm = true;
        if (_alarm) Emit("[MSG:'$H'|'$X' to unlock]");
    }

    string? SoftLimitAlarm()
    {
        _alarm = true; _hold = false;
        _planner.Clear(); _rx.Clear(); _rxBytes = 0;
        _px = _mx; _pz = _mz;
        Emit("ALARM:2");
        Emit("[MSG:Reset to continue]");
        return null;
    }

    bool OutOfTravel(double x, double z) =>
        x > 1e-6 || x < -_o.TravelXmm - 1e-6 || z > 1e-6 || z < -_o.TravelZmm - 1e-6;

    // ------------------------------------------------------------------ line execution

    string? Execute(string raw)
    {
        var line = Streamer.Clean(raw);
        if (line.Length == 0) return "ok";
        if (line[0] == '$') return SystemCommand(line);
        if (_alarm) return "error:9";
        return Gcode(line, jog: false);
    }

    string? SystemCommand(string line)
    {
        bool busy = _planner.Count > 0 || _hold;
        switch (line)
        {
            case "$":
                Emit("[HLP:$$ $# $G $I $N $x=val $Nx=line $J=line $C $X $H ~ ! ? ctrl-x]");
                return "ok";
            case "$$":
                foreach (var kv in _settings) Emit($"${kv.Key}={kv.Value}");
                return "ok";
            case "$#":
                {
                    double k = _m.Diameter ? 2 : 1;
                    Emit($"[G54:{F3(_wcoX * k)},0.000,{F3(_wcoZ)}]");
                    foreach (var g in new[] { "G55", "G56", "G57", "G58", "G59", "G28", "G30", "G92" })
                        Emit($"[{g}:0.000,0.000,0.000]");
                    Emit("[TLO:0.000]");
                    return "ok";
                }
            case "$G":
                Emit($"[GC:G{_m.Motion} G54 G18 {(_m.Inches ? "G20" : "G21")} {(_m.Incremental ? "G91" : "G90")} " +
                     $"{(_m.FeedPerRev ? "G95" : "G94")} {(_m.Diameter ? "G7" : "G8")} {(_m.Css ? "G96" : "G97")} " +
                     $"{(_m.SpOn ? "M" + _m.SpDir : "M5")} {(_m.Cool ? "M8" : "M9")} T0 F{_m.F.ToString("0.###", CI)} S{_m.S.ToString("0.###", CI)}]");
                return "ok";
            case "$I":
                Emit("[VER:1.1f.20250101:LatheStudio virtual lathe]");
                Emit($"[OPT:VNMSL,{PlannerSize},{_o.RxBufferSize},3,0]");
                Emit("[NEWOPT:ENUMS,RT+,HOME,LATHE" + (_o.SpindleSync ? ",SS" : "") + "]");
                Emit("[FIRMWARE:grblHAL]");
                return "ok";
            case "$H":
                if (Setting(22) != "1") return "error:5";
                if (busy || _check) return "error:8";
                _mx = _mz = _px = _pz = 0;
                _homed = true; _alarm = false;
                return "ok";
            case "$X":
                if (_alarm) { _alarm = false; Emit("[MSG:Caution: Unlocked]"); }
                return "ok";
            case "$C":
                if (busy) return "error:8";
                if (!_check)
                {
                    _check = true; _alarm = false;
                    Emit("[MSG:Enabled]");
                    return "ok";
                }
                Emit("[MSG:Disabled]");
                Emit("ok");
                Reset(); // grbl resets when leaving check mode
                return null;
        }
        if (line.StartsWith("$J=", StringComparison.Ordinal))
        {
            if (_alarm) return "error:9";
            if (_check || _hold || (_planner.Count > 0 && _planner.First!.Value.Jog == false)) return "error:8";
            return Gcode(line[3..], jog: true);
        }
        if (line.StartsWith("$N", StringComparison.Ordinal))
        {
            if (line == "$N") { Emit("$N0="); Emit("$N1="); }
            return "ok";
        }
        if (GrblParser.TryParseSetting(line, out var id, out var value))
        {
            if (busy) return "error:8";
            if (!double.TryParse(value, NumberStyles.Float, CI, out _)) return "error:2";
            _settings[id] = value;
            return "ok";
        }
        return "error:3";
    }

    string? Gcode(string line, bool jog)
    {
        // ---- tokenize
        var words = new List<(char L, double V)>();
        int i = 0;
        while (i < line.Length)
        {
            char letter = line[i++];
            if (letter < 'A' || letter > 'Z') return "error:1";
            int s = i;
            if (i < line.Length && (line[i] == '-' || line[i] == '+')) i++;
            while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] == '.')) i++;
            if (!double.TryParse(line.AsSpan(s, i - s), NumberStyles.Float, CI, out var v)) return "error:2";
            words.Add((letter, v));
        }

        var m = _m.Clone();
        int? motion = null, spindle = null;
        bool g4 = false, g10 = false, g28 = false, g53 = false, pause = false, end = false;
        bool? cool = null;
        var val = new Dictionary<char, double>();
        foreach (var (letter, v) in words)
        {
            if (letter is 'G' or 'M' && v != Math.Floor(v)) return "error:20";
            int code = (int)v;
            if (jog)
            {
                if (letter == 'G' && code is not (20 or 21 or 90 or 91 or 53)) return "error:16";
                if (letter is not ('G' or 'X' or 'Z' or 'F')) return "error:16";
            }
            if (letter == 'G')
            {
                switch (code)
                {
                    case 0: case 1: case 2: case 3: case 33: case 76: case 80: motion = code; break;
                    case 4: g4 = true; break;
                    case 10: g10 = true; break;
                    case 28: g28 = true; break;
                    case 53: g53 = true; break;
                    case 7: m.Diameter = true; break;
                    case 8: m.Diameter = false; break;
                    case 17: case 18: case 19: case 40: case 49: case 54: case 61: break;
                    case 20: m.Inches = true; break;
                    case 21: m.Inches = false; break;
                    case 90: m.Incremental = false; break;
                    case 91: m.Incremental = true; break;
                    case 94: m.FeedPerRev = false; break;
                    case 95: m.FeedPerRev = true; break;
                    case 96: m.Css = true; break;
                    case 97: m.Css = false; break;
                    default: return "error:20";
                }
            }
            else if (letter == 'M')
            {
                switch (code)
                {
                    case 0: pause = true; break;
                    case 1: break;
                    case 2: case 30: end = true; break;
                    case 3: case 4: case 5: spindle = code; break;
                    case 8: cool = true; break;
                    case 9: cool = false; break;
                    default: return "error:20";
                }
            }
            else if ("XZFSTNPLIKRQHEDJ".Contains(letter))
            {
                if (!val.TryAdd(letter, v)) return "error:25";
            }
            else return "error:20";
        }

        double u = m.Inches ? 25.4 : 1;
        double xs = m.Diameter ? 0.5 : 1; // X word -> radius
        if (val.TryGetValue('F', out var fw)) { if (fw < 0) return "error:4"; m.F = fw * u; }
        if (val.TryGetValue('S', out var sw)) { if (sw < 0) return "error:4"; m.S = sw; }
        if (val.TryGetValue('D', out var dw)) m.CssMax = dw;

        var queue = new List<Block>();
        bool spindleChanged = false;
        if (spindle is 3 or 4) { m.SpOn = true; m.SpDir = spindle.Value; spindleChanged = true; }
        else if (spindle == 5) { m.SpOn = false; spindleChanged = true; }
        else if (val.ContainsKey('S') && m.SpOn) spindleChanged = true;
        if (spindleChanged)
            queue.Add(new Block { Kind = Kind.Spindle, On = m.SpOn, Dir = m.SpDir, Value = m.SpOn ? Rpm(m, _px) : 0 });
        if (cool.HasValue) { m.Cool = cool.Value; queue.Add(new Block { Kind = Kind.Coolant, On = cool.Value }); }
        if (g4)
        {
            if (!val.TryGetValue('P', out var p) || p < 0) return "error:28";
            queue.Add(new Block { Kind = Kind.Dwell, Value = p });
        }

        bool hasX = val.TryGetValue('X', out var xv), hasZ = val.TryGetValue('Z', out var zv);
        double px = _px, pz = _pz;
        bool wcoChanged = false;
        double nwx = _wcoX, nwz = _wcoZ;

        if (g10)
        {
            if (!val.TryGetValue('L', out var l) || !val.TryGetValue('P', out var pp)) return "error:28";
            if (pp != 0 && pp != 1) return "error:29";
            if (l == 2) { if (hasX) nwx = xv * u * xs; if (hasZ) nwz = zv * u; }
            else if (l == 20) { if (hasX) nwx = _px - xv * u * xs; if (hasZ) nwz = _pz - zv * u; }
            else return "error:20";
            wcoChanged = true;
        }
        else if (g28)
        {
            if (hasX || hasZ)
            {
                double ix = hasX ? (m.Incremental ? _px + xv * u * xs : _wcoX + xv * u * xs) : _px;
                double iz = hasZ ? (m.Incremental ? _pz + zv * u : _wcoZ + zv * u) : _pz;
                if (SoftLimits && OutOfTravel(ix, iz)) return SoftLimitAlarm();
                queue.Add(new Block { Kind = Kind.Move, X = ix, Z = iz, Speed = RapidMmMin, Rapid = true });
            }
            queue.Add(new Block { Kind = Kind.Move, X = 0, Z = 0, Speed = RapidMmMin, Rapid = true });
            px = 0; pz = 0;
        }
        else
        {
            int mm = jog ? 1 : motion ?? m.Motion;
            if (motion.HasValue && !jog) m.Motion = motion.Value;
            if (hasX || hasZ)
            {
                if (mm == 80) return "error:31";
                double tx = !hasX ? _px : g53 ? xv * u * xs : m.Incremental ? _px + xv * u * xs : _wcoX + xv * u * xs;
                double tz = !hasZ ? _pz : g53 ? zv * u : m.Incremental ? _pz + zv * u : _wcoZ + zv * u;
                if (SoftLimits && OutOfTravel(tx, tz)) return jog ? "error:15" : SoftLimitAlarm();
                double speed;
                string? err;
                switch (mm)
                {
                    case 0:
                        queue.Add(new Block { Kind = Kind.Move, X = tx, Z = tz, Speed = RapidMmMin, Rapid = true });
                        break;
                    case 1:
                        if (jog)
                        {
                            if (!val.ContainsKey('F') || m.F <= 0) return "error:16";
                            queue.Add(new Block { Kind = Kind.Move, X = tx, Z = tz, Speed = m.F, Jog = true });
                            break;
                        }
                        if ((err = FeedRate(m, tx, out speed)) != null) return err;
                        queue.Add(new Block { Kind = Kind.Move, X = tx, Z = tz, Speed = speed });
                        break;
                    case 2:
                    case 3:
                        {
                            double ex = tx - _px, ez = tz - _pz;
                            if (val.TryGetValue('R', out var r))
                            {
                                double chord = Math.Sqrt(ex * ex + ez * ez);
                                if (chord / 2 > Math.Abs(r) * u + 0.005) return "error:33";
                            }
                            else
                            {
                                bool hi = val.TryGetValue('I', out var ii), hk = val.TryGetValue('K', out var kk);
                                if (!hi && !hk) return "error:35";
                                double cx = _px + ii * u, cz = _pz + kk * u;
                                double r1 = Math.Sqrt((_px - cx) * (_px - cx) + (_pz - cz) * (_pz - cz));
                                double r2 = Math.Sqrt((tx - cx) * (tx - cx) + (tz - cz) * (tz - cz));
                                double dr = Math.Abs(r1 - r2);
                                if (dr > 0.005 && dr > 0.001 * r1) return "error:33";
                            }
                            if ((err = FeedRate(m, tx, out speed)) != null) return err;
                            queue.Add(new Block { Kind = Kind.Move, X = tx, Z = tz, Speed = speed });
                            break;
                        }
                    case 33:
                        {
                            if (!_o.SpindleSync) return "error:20";
                            if (!val.TryGetValue('K', out var k) || k <= 0) return "error:28";
                            if (!m.SpOn || m.S <= 0) return "error:41";
                            queue.Add(new Block { Kind = Kind.Move, X = tx, Z = tz, Speed = Rpm(m, tx) * k * u });
                            break;
                        }
                    case 76:
                        {
                            if (!_o.SpindleSync) return "error:20";
                            if (!val.TryGetValue('P', out var pitch) || pitch <= 0 || !hasZ) return "error:28";
                            if (!m.SpOn || m.S <= 0) return "error:41";
                            double sp = Rpm(m, _px) * pitch * u;
                            queue.Add(new Block { Kind = Kind.Move, X = _px, Z = tz, Speed = sp });
                            queue.Add(new Block { Kind = Kind.Move, X = _px, Z = _pz, Speed = RapidMmMin, Rapid = true });
                            tx = _px; tz = _pz; // cycle ends where it started
                            break;
                        }
                }
                px = tx; pz = tz;
            }
            else if (motion is 2 or 3 or 33 or 76) return "error:26";
        }

        if (pause) queue.Add(new Block { Kind = Kind.Pause });
        if (end)
        {
            queue.Add(new Block { Kind = Kind.End });
            m.Motion = 1; m.Incremental = false; m.FeedPerRev = false; m.Css = false; m.SpOn = false; m.Cool = false;
        }

        // ---- commit (nothing above has side effects, so an error leaves state untouched)
        if (!jog) _m = m;
        _px = px; _pz = pz;
        if (_check) return "ok";
        if (wcoChanged) { _wcoX = nwx; _wcoZ = nwz; _wcoDirty = true; }
        foreach (var b in queue) _planner.AddLast(b);
        return "ok";
    }

    string? FeedRate(Modal m, double targetX, out double speed)
    {
        speed = 0;
        if (m.FeedPerRev)
        {
            if (!m.SpOn) return "error:41";
            double rpm = Rpm(m, targetX);
            if (rpm <= 0) return "error:41";
            if (m.F <= 0) return "error:22";
            speed = m.F * rpm;
            return null;
        }
        if (m.F <= 0) return "error:22";
        speed = m.F;
        return null;
    }

    double Rpm(Modal m, double machineX)
    {
        double max = _o.MaxRpm;
        if (!m.Css) return Math.Min(m.S, max);
        if (m.CssMax > 0) max = Math.Min(max, m.CssMax);
        double surface = m.Inches ? m.S * 0.3048 : m.S;          // m/min
        double d = Math.Max(Math.Abs(machineX - _wcoX) * 2, 1.0); // mm
        return Math.Min(max, 1000 * surface / (Math.PI * d));
    }
}
