using System.Globalization;

namespace Lathe.Machine;

public sealed record ControllerInfo(string Version, string Options, int RxBufferSize, bool HasSpindleSync, bool IsLatheMode,
    IReadOnlyDictionary<int, string> Settings);

public sealed record ConnectReport(bool Ok, ControllerInfo? Info, IReadOnlyList<string> Warnings, string? Error);

/// <summary>
/// High-level grblHAL connection: handshake, status polling + watchdog, ok/error bookkeeping, MDI, jog,
/// real-time commands. All events are raised on background threads; UI code must marshal.
/// </summary>
public sealed class GrblController : IDisposable
{
    internal readonly record struct Ack(bool Ok, int Code, string? Error, IReadOnlyList<string> Lines);

    sealed class Pending(string line, Action<Ack> done)
    {
        public readonly string Line = line;
        public readonly Action<Ack> Done = done;
        public readonly List<string> Captured = new();
    }

    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    const int PollMs = 200;
    const int WatchdogMs = 1000;

    readonly object _lock = new();      // guards _fifo, _awaitWelcome, _welcome
    readonly object _sendLock = new();  // keeps FIFO order == wire order
    readonly LinkedList<Pending> _fifo = new();
    TaskCompletionSource<string> _welcome = NewTcs();
    bool _awaitWelcome;
    Timer? _poll;
    long _lastStatusTicks;
    long _statusSeq;
    (double X, double Z)? _wco;
    volatile bool _connected;
    volatile GrblStatus? _status;
    volatile Streamer? _active;
    volatile bool _homed;
    bool _disposed;

    public GrblController(IMachineTransport transport)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Transport.LineReceived += OnLine;
        Transport.Faulted += OnFault;
    }

    public IMachineTransport Transport { get; }
    public GrblStatus? Status => _status;
    public ControllerInfo? Info { get; private set; }
    public bool IsConnected => _connected;
    /// <summary>True after a successful $H; cleared by any alarm or unexpected controller restart.</summary>
    public bool IsHomed => _homed;
    public bool IsStreaming => _active != null;

    public event Action<GrblStatus>? StatusChanged;
    public event Action<string>? Console;
    public event Action<int, string>? AlarmRaised;
    public event Action<int, string>? ErrorRaised;
    public event Action<string>? ConnectionLost;

    internal long StatusSeq => Interlocked.Read(ref _statusSeq);

    static TaskCompletionSource<string> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ------------------------------------------------------------------ connect

    public async Task<ConnectReport> ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return new ConnectReport(true, Info, [], null);
        var warnings = new List<string>();
        try
        {
            var first = NewTcs();
            lock (_lock) { _welcome = first; _awaitWelcome = false; _fifo.Clear(); }
            try { Transport.Open(); }
            catch (Exception ex) { return Fail($"Could not open the port: {ex.Message}"); }

            // Boards that reboot on open print a welcome on their own; give them a moment.
            await Task.WhenAny(first.Task, Task.Delay(300, ct));

            SoftResetInternal("connecting");
            Task<string> w;
            lock (_lock) w = _welcome.Task;
            if (await Task.WhenAny(w, Task.Delay(5000, ct)) != w)
                return Fail("No response from the controller (no Grbl welcome message). Check the port, cable and baud rate.");
            string welcomeLine = w.Result;

            Ack info = default;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                info = await SendAsync("$I", TimeSpan.FromSeconds(3));
                if (info.Ok || info.Code != -1) break; // -1 = controller restarted mid-query: retry once
            }
            if (!info.Ok) return Fail($"Controller did not answer $I: {info.Error}");

            var settingsAck = await SendAsync("$$", TimeSpan.FromSeconds(3));
            if (!settingsAck.Ok) return Fail($"Controller did not answer $$: {settingsAck.Error}");
            var settings = new SortedDictionary<int, string>();
            foreach (var l in settingsAck.Lines)
                if (GrblParser.TryParseSetting(l, out var id, out var v)) settings[id] = v;

            Info = BuildInfo(info.Lines, settings);
            bool isHal = welcomeLine.Contains("GrblHAL", StringComparison.OrdinalIgnoreCase) ||
                         info.Lines.Any(l => l.Contains("FIRMWARE:grblHAL", StringComparison.OrdinalIgnoreCase));

            // First status report, then start polling + watchdog.
            long seq = StatusSeq;
            Transport.WriteRealtime((byte)'?');
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (StatusSeq == seq && sw.ElapsedMilliseconds < 1500) await Task.Delay(10, ct);
            if (StatusSeq == seq) return Fail("Controller does not answer status requests ('?').");

            _homed = false;
            Interlocked.Exchange(ref _lastStatusTicks, Environment.TickCount64);
            _connected = true;
            _poll = new Timer(_ => Poll(), null, PollMs, PollMs);

            if (!isHal) warnings.Add("Controller does not identify as grblHAL. Lathe features (G7, G33/G76, G95) may not be available.");
            if (!Info.IsLatheMode) warnings.Add("Controller is not in lathe mode ($32=2). Diameter mode (G7) and lathe cycles may not work.");
            if (settings.TryGetValue(22, out var hom) && hom.Trim() == "0") warnings.Add("Homing is disabled ($22=0). Machine position cannot be trusted after power-up and soft limits are unreliable.");
            if (settings.TryGetValue(20, out var sl) && sl.Trim() == "0") warnings.Add("Soft limits are off ($20=0). The controller will not stop moves beyond machine travel.");
            if (!Info.HasSpindleSync) warnings.Add("No spindle sync reported. Threading (G33/G76) and feed-per-rev (G95) are disabled.");
            if (_status?.State == MachineState.Alarm) warnings.Add("Controller is in ALARM state. Home ($H) or unlock ($X) before moving.");

            return new ConnectReport(true, Info, warnings, null);
        }
        catch (OperationCanceledException)
        {
            return Fail("Connect cancelled.");
        }

        ConnectReport Fail(string error)
        {
            _connected = false;
            _poll?.Dispose(); _poll = null;
            try { Transport.Close(); } catch { }
            return new ConnectReport(false, Info, warnings, error);
        }
    }

    static ControllerInfo BuildInfo(IReadOnlyList<string> lines, IReadOnlyDictionary<int, string> settings)
    {
        string ver = "", opt = "", newopt = "";
        bool sync = false;
        foreach (var l in lines)
        {
            if (l.StartsWith("[VER:", StringComparison.OrdinalIgnoreCase)) ver = Inner(l, 5);
            else if (l.StartsWith("[OPT:", StringComparison.OrdinalIgnoreCase)) opt = Inner(l, 5);
            else if (l.StartsWith("[NEWOPT:", StringComparison.OrdinalIgnoreCase)) newopt = Inner(l, 8);
            if (l.Contains("SPINDLE SYNC", StringComparison.OrdinalIgnoreCase)) sync = true;
        }
        var tokens = newopt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        sync |= tokens.Any(t => t.Equals("SS", StringComparison.OrdinalIgnoreCase) || t.Equals("SPINDLESYNC", StringComparison.OrdinalIgnoreCase));
        bool lathe = (settings.TryGetValue(32, out var mode) && mode.Trim() == "2") ||
                     tokens.Any(t => t.Equals("LATHE", StringComparison.OrdinalIgnoreCase));
        int rx = 128;
        var parts = opt.Split(',');
        if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CI, out var r) && r > 0) rx = r;
        var options = newopt.Length > 0 ? $"{opt}|{newopt}" : opt;
        return new ControllerInfo(ver, options, rx, sync, lathe, settings);

        static string Inner(string l, int start) => l.Length > start ? l[start..].TrimEnd(']') : "";
    }

    public void Disconnect()
    {
        if (_connected && (IsStreaming || _status?.State is MachineState.Run or MachineState.Jog))
        {
            // Never leave a machine moving behind us.
            TryRealtime((byte)'!');
            Thread.Sleep(200);
            TryRealtime(0x18);
        }
        _connected = false;
        _poll?.Dispose(); _poll = null;
        FailAll("Disconnected.");
        try { Transport.Close(); } catch { }
    }

    // ------------------------------------------------------------------ receive

    void OnLine(string line)
    {
        if (line.Length == 0) return;
        if (line[0] == '<')
        {
            var s = GrblParser.ParseStatus(line, _wco);
            if (s == null) return;
            if (s.HasWco) _wco = (s.MX - s.WX, s.MZ - s.WZ);
            _status = s;
            Interlocked.Exchange(ref _lastStatusTicks, Environment.TickCount64);
            Interlocked.Increment(ref _statusSeq);
            StatusChanged?.Invoke(s);
            return;
        }

        Console?.Invoke(line);

        if (line.StartsWith("Grbl", StringComparison.OrdinalIgnoreCase))
        {
            HandleWelcome(line);
            return;
        }

        bool isOk = line == "ok";
        if (isOk || line.StartsWith("error:", StringComparison.Ordinal))
        {
            int code = 0;
            if (!isOk) int.TryParse(line.AsSpan(6), NumberStyles.Integer, CI, out code);
            Pending? p = null;
            lock (_lock)
            {
                if (_awaitWelcome) return; // stale ack from before a reset
                if (_fifo.First != null) { p = _fifo.First.Value; _fifo.RemoveFirst(); }
            }
            if (!isOk) ErrorRaised?.Invoke(code, GrblCodes.Error(code));
            p?.Done(new Ack(isOk, isOk ? 0 : code, isOk ? null : $"error:{code} – {GrblCodes.Error(code)}", p.Captured));
            return;
        }

        if (line.StartsWith("ALARM:", StringComparison.Ordinal))
        {
            int.TryParse(line.AsSpan(6), NumberStyles.Integer, CI, out var code);
            _homed = false;
            AlarmRaised?.Invoke(code, GrblCodes.Alarm(code));
            bool pending;
            lock (_lock) pending = _fifo.Count > 0;
            // Outside a program run: resynchronise so stray acks can't be matched to later commands.
            if (pending && _active == null) SoftResetInternal($"ALARM:{code} – {GrblCodes.Alarm(code)}");
            return;
        }

        lock (_lock)
        {
            if (_fifo.First != null) _fifo.First.Value.Captured.Add(line);
        }
    }

    void HandleWelcome(string line)
    {
        List<Pending>? failed = null;
        TaskCompletionSource<string> tcs;
        lock (_lock)
        {
            if (_awaitWelcome) _awaitWelcome = false;
            else if (_fifo.Count > 0) { failed = [.. _fifo]; _fifo.Clear(); }
            tcs = _welcome;
        }
        if (failed != null)
        {
            _homed = false;
            foreach (var f in failed) f.Done(new Ack(false, -1, "Controller restarted unexpectedly.", f.Captured));
        }
        tcs.TrySetResult(line);
    }

    void OnFault(Exception ex) => HandleLost($"Connection error: {ex.Message}");

    void Poll()
    {
        if (!_connected) return;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastStatusTicks) > WatchdogMs)
        {
            HandleLost("No response from the controller for 1 s (cable unplugged or controller frozen).");
            return;
        }
        try { Transport.WriteRealtime((byte)'?'); }
        catch (Exception ex) { HandleLost($"Connection error: {ex.Message}"); }
    }

    void HandleLost(string reason)
    {
        if (!_connected) return;
        _connected = false;
        _poll?.Dispose(); _poll = null;
        FailAll(reason);
        try { Transport.Close(); } catch { }
        ConnectionLost?.Invoke(reason);
    }

    void FailAll(string reason)
    {
        List<Pending> failed;
        lock (_lock) { failed = [.. _fifo]; _fifo.Clear(); }
        foreach (var f in failed) f.Done(new Ack(false, -1, reason, f.Captured));
    }

    // ------------------------------------------------------------------ send

    /// <summary>Queues a line; <paramref name="done"/> is invoked with its ok/error (or a failure if reset/lost).</summary>
    internal bool SendStreamLine(string line, Action<Ack> done)
    {
        var p = new Pending(line, done);
        lock (_sendLock)
        {
            lock (_lock) _fifo.AddLast(p);
            Console?.Invoke("> " + line);
            try { Transport.WriteLine(line); }
            catch (Exception ex)
            {
                lock (_lock) _fifo.Remove(p);
                HandleLost($"Write failed: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    internal async Task<Ack> SendAsync(string line, TimeSpan timeout)
    {
        Task<string> w;
        bool waiting;
        lock (_lock) { waiting = _awaitWelcome; w = _welcome.Task; }
        if (waiting) await Task.WhenAny(w, Task.Delay(3000));

        var tcs = new TaskCompletionSource<Ack>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!SendStreamLine(line, a => tcs.TrySetResult(a)))
            return new Ack(false, -1, "Not connected (write failed).", []);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return done == tcs.Task ? tcs.Task.Result : new Ack(false, -2, "Timed out waiting for the controller.", []);
    }

    /// <summary>Returns a task that completes on the next welcome banner (used around $C, which resets).</summary>
    internal Task ArmWelcome()
    {
        lock (_lock) { _welcome = NewTcs(); return _welcome.Task; }
    }

    internal bool TryBeginStream(Streamer s) => Interlocked.CompareExchange(ref _active, s, null) == null;
    internal void EndStream(Streamer s) => Interlocked.CompareExchange(ref _active, null, s);

    public async Task<(bool ok, string? error)> SendCommandAsync(string line, TimeSpan? timeout = null)
    {
        if (!_connected) return (false, "Not connected.");
        if (IsStreaming) return (false, "A program is running. Stop or wait for it to finish.");
        if (string.IsNullOrWhiteSpace(line)) return (false, "Empty command.");
        if (line.Length > Streamer.MaxLineLength) return (false, "Command is too long.");
        var a = await SendAsync(line.Trim(), timeout ?? DefaultTimeout);
        return (a.Ok, a.Error);
    }

    public async Task<(bool ok, string? error)> HomeAsync()
    {
        var r = await SendCommandAsync("$H", TimeSpan.FromMinutes(2));
        if (r.ok) _homed = true;
        return r;
    }

    public Task<(bool ok, string? error)> UnlockAsync() => SendCommandAsync("$X");

    /// <summary>Incremental jog. With G7 active (lathe default) X distances are diameters.</summary>
    public void Jog(char axis, double distance, double feed)
    {
        axis = char.ToUpperInvariant(axis);
        if (!_connected || IsStreaming || axis is not ('X' or 'Z') || feed <= 0 || double.IsNaN(distance) || distance == 0) return;
        if (_status?.State is not (MachineState.Idle or MachineState.Jog))
        {
            Console?.Invoke($"[jog refused: machine is {_status?.State}]");
            return;
        }
        var cmd = string.Create(CI, $"$J=G91 G21 {axis}{distance:0.####} F{feed:0.###}");
        SendStreamLine(cmd, a => { /* errors are reported via ErrorRaised */ });
    }

    public void JogCancel() => TryRealtime(0x85);
    public void FeedHold() => TryRealtime((byte)'!');
    public void CycleStart() => TryRealtime((byte)'~');
    public void SoftReset() => SoftResetInternal("Controller reset.");

    void SoftResetInternal(string reason)
    {
        List<Pending> failed;
        TaskCompletionSource<string> tcs;
        lock (_lock)
        {
            _awaitWelcome = true;
            _welcome = tcs = NewTcs();
            failed = [.. _fifo];
            _fifo.Clear();
        }
        TryRealtime(0x18);
        foreach (var f in failed) f.Done(new Ack(false, -1, reason, f.Captured));
        // Safety net: if no banner ever arrives, stop discarding acks after 3 s.
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            lock (_lock) if (_welcome == tcs && _awaitWelcome) _awaitWelcome = false;
        }, TaskScheduler.Default);
    }

    public void FeedOverride(int delta)
    {
        byte? b = delta switch { 0 => 0x90, 10 => 0x91, -10 => 0x92, 1 => 0x93, -1 => 0x94, _ => null };
        if (b.HasValue) TryRealtime(b.Value);
    }

    public void SpindleOverride(int delta)
    {
        byte? b = delta switch { 0 => 0x99, 10 => 0x9A, -10 => 0x9B, 1 => 0x9C, -1 => 0x9D, _ => null };
        if (b.HasValue) TryRealtime(b.Value);
    }

    public void RapidOverride(int pct)
    {
        byte? b = pct switch { 100 => 0x95, 50 => 0x96, 25 => 0x97, _ => null };
        if (b.HasValue) TryRealtime(b.Value);
    }

    void TryRealtime(byte b)
    {
        if (!Transport.IsOpen) return;
        try { Transport.WriteRealtime(b); }
        catch (Exception ex) { HandleLost($"Connection error: {ex.Message}"); }
    }

    public Streamer CreateStreamer() => new(this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        Transport.LineReceived -= OnLine;
        Transport.Faulted -= OnFault;
    }
}
