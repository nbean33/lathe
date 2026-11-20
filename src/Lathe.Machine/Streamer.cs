using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lathe.Machine;

/// <summary>
/// Streams a G-code program with character-counting flow control. Fail-safe: on any error or alarm it
/// feed-holds and soft-resets the controller and never sends another line.
/// App directives (never sent): <c>(LS:OP n name)</c> marks an operation start, <c>(LS:TOOL n)</c> is a tool-change
/// point (the streamer itself drains and waits for Idle there, so do not put an M0 in front of it).
/// </summary>
public sealed partial class Streamer
{
    public const int MaxLineLength = 255;

    enum ItemKind { Code, Op, Tool }
    readonly record struct Item(int Src, ItemKind Kind, string Text, int Num);

    [GeneratedRegex(@"^\(\s*LS:OP\s+(\d+)\b[^)]*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpRx();
    [GeneratedRegex(@"^\(\s*LS:TOOL\s+(\d+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ToolRx();

    readonly GrblController _c;
    readonly object _lock = new();
    readonly Queue<(int Len, int Src)> _inflight = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly List<string> _errors = new();
    int _inflightBytes;
    int _limit;
    int _running;
    bool _check;
    volatile string? _fault;
    Task? _stopTask;
    int _acked, _currentOp, _currentSrc = -1;

    internal Streamer(GrblController controller) { _c = controller; }

    public int TotalLines { get; private set; }
    public int AckedLines => Volatile.Read(ref _acked);
    public int CurrentOpIndex => Volatile.Read(ref _currentOp);
    public int CurrentSourceLine => Volatile.Read(ref _currentSrc);
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    /// <summary>Errors collected by <see cref="CheckAsync"/>.</summary>
    public IReadOnlyList<string> Errors { get { lock (_lock) return [.. _errors]; } }

    public Func<int, CancellationToken, Task<IReadOnlyList<string>?>>? ToolChangeHandler { get; set; }

    public event Action<Streamer>? Progress;
    public event Action? Completed;
    public event Action<string>? Aborted;

    public Task RunAsync(IReadOnlyList<string> program, int startOpIndex = 0, IReadOnlyList<string>? preamble = null,
        CancellationToken ct = default) => RunCoreAsync(program, startOpIndex, preamble, check: false, ct);

    public Task CheckAsync(IReadOnlyList<string> program, CancellationToken ct = default)
        => RunCoreAsync(program, 0, null, check: true, ct);

    /// <summary>Feed hold, wait (≤1 s) for the machine to stop, then soft reset. Raises Aborted("stopped by user").</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        lock (_lock)
        {
            if (_fault != null) return;
            _fault = "stopped by user";
            _stopTask = Task.Run(async () =>
            {
                _c.FeedHold();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1000)
                {
                    if (_c.Status?.State is not (MachineState.Run or MachineState.Jog)) break;
                    await Task.Delay(20);
                }
                _c.SoftReset();
            });
        }
        _signal.Release();
    }

    async Task RunCoreAsync(IReadOnlyList<string> program, int startOpIndex, IReadOnlyList<string>? preamble, bool check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("This streamer is already running.");

        lock (_lock)
        {
            _fault = null; _stopTask = null; _errors.Clear(); _inflight.Clear(); _inflightBytes = 0;
        }
        _acked = 0; _currentOp = 0; _currentSrc = -1; _check = check;
        while (_signal.CurrentCount > 0) _signal.Wait(0);
        _limit = Math.Max(16, Math.Min(_c.Info?.RxBufferSize ?? 128, 1024) - 1);

        // ---- prepare + validate everything before sending a single byte
        var items = Prepare(program, 0);
        if (startOpIndex > 0)
        {
            int idx = items.FindIndex(i => i.Kind == ItemKind.Op && i.Num == startOpIndex);
            if (idx < 0) { EarlyAbort($"Operation {startOpIndex} not found in the program."); return; }
            items = items.GetRange(idx, items.Count - idx);
        }
        if (preamble != null) items.InsertRange(0, Prepare(preamble, -1).Where(i => i.Kind == ItemKind.Code).Select(i => i with { Src = -1 }));

        int maxLen = Math.Min(MaxLineLength, _limit - 1);
        foreach (var it in items)
            if (it.Kind == ItemKind.Code && it.Text.Length > maxLen)
            {
                EarlyAbort($"Line {(it.Src >= 0 ? (it.Src + 1).ToString(CultureInfo.InvariantCulture) : "(preamble)")} is too long ({it.Text.Length} characters, max {maxLen}).");
                return;
            }
        TotalLines = items.Count(i => i.Kind == ItemKind.Code);

        if (!_c.IsConnected) { EarlyAbort("Not connected to the controller."); return; }
        if (!_c.TryBeginStream(this)) { EarlyAbort("Another program is already running."); return; }

        _c.ConnectionLost += OnLost;
        _c.AlarmRaised += OnAlarm;
        try
        {
            using (ct.Register(Stop))
            {
                if (check)
                {
                    var a = await _c.SendAsync("$C", TimeSpan.FromSeconds(3));
                    if (!a.Ok) Fault($"Could not enter check mode: {a.Error}", false);
                }
                await LoopAsync(items, ct);
                if (check && _fault == null && _c.IsConnected)
                {
                    var w = _c.ArmWelcome();
                    await _c.SendAsync("$C", TimeSpan.FromSeconds(3)); // leaving check mode resets the controller
                    await Task.WhenAny(w, Task.Delay(3000));
                }
            }
            Task? stop;
            lock (_lock) stop = _stopTask;
            if (stop != null) await stop;
        }
        finally
        {
            _c.ConnectionLost -= OnLost;
            _c.AlarmRaised -= OnAlarm;
            _c.EndStream(this);
            Volatile.Write(ref _running, 0);
        }

        if (_fault != null) Aborted?.Invoke(_fault);
        else if (check && _errors.Count > 0) Aborted?.Invoke($"Check found {_errors.Count} error(s). First: {_errors[0]}");
        else Completed?.Invoke();
    }

    void EarlyAbort(string reason)
    {
        Volatile.Write(ref _running, 0);
        Aborted?.Invoke(reason);
    }

    async Task LoopAsync(List<Item> items, CancellationToken ct)
    {
        foreach (var it in items)
        {
            if (_fault != null) return;
            switch (it.Kind)
            {
                case ItemKind.Op:
                    Volatile.Write(ref _currentOp, it.Num);
                    Progress?.Invoke(this);
                    break;

                case ItemKind.Tool:
                    if (_check) break;
                    if (!await DrainAsync() || !await WaitIdleAsync()) return;
                    var handler = ToolChangeHandler;
                    if (handler == null) { Fault($"Tool change to T{it.Num} needed but no tool-change handler is set.", false); return; }
                    IReadOnlyList<string>? extra;
                    try { extra = await handler(it.Num, ct); }
                    catch (Exception ex) { Fault($"Tool change to T{it.Num} failed: {ex.Message}", false); return; }
                    if (_fault != null) return;
                    if (extra == null) { Fault($"Tool change to T{it.Num} cancelled.", false); return; }
                    foreach (var raw in extra)
                    {
                        var t = Clean(raw);
                        if (t.Length == 0) continue;
                        if (t.Length > Math.Min(MaxLineLength, _limit - 1)) { Fault($"Tool-change line too long: {t}", false); return; }
                        TotalLines++;
                        if (!await SendOneAsync(t, it.Src)) return;
                    }
                    break;

                default:
                    if (!await SendOneAsync(it.Text, it.Src)) return;
                    break;
            }
        }
        if (!await DrainAsync()) return;
        if (!_check) await WaitIdleAsync();
    }

    async Task<bool> SendOneAsync(string text, int src)
    {
        int len = text.Length + 1;
        while (true)
        {
            if (_fault != null) return false;
            lock (_lock)
            {
                if (_inflightBytes + len <= _limit)
                {
                    _inflight.Enqueue((len, src));
                    _inflightBytes += len;
                    break;
                }
            }
            await _signal.WaitAsync(50);
        }
        if (!_c.SendStreamLine(text, a => OnAck(a, text, src)))
        {
            Fault("Could not send to the controller (connection lost).", false);
            return false;
        }
        return true;
    }

    void OnAck(GrblController.Ack a, string text, int src)
    {
        lock (_lock)
        {
            if (_inflight.Count > 0) { var e = _inflight.Dequeue(); _inflightBytes -= e.Len; }
        }
        if (a.Code >= 0)
        {
            Interlocked.Increment(ref _acked);
            if (src >= 0) Volatile.Write(ref _currentSrc, src);
        }
        if (!a.Ok)
        {
            var where = src >= 0 ? $"line {src + 1}" : "preamble";
            if (a.Code < 0) Fault(a.Error ?? "Controller reset.", false);
            else if (_check) lock (_lock) _errors.Add($"{where}: {text} → error:{a.Code} ({GrblCodes.Error(a.Code)})");
            else Fault($"error:{a.Code} ({GrblCodes.Error(a.Code)}) at {where}: {text}", true);
        }
        Progress?.Invoke(this);
        _signal.Release();
    }

    async Task<bool> DrainAsync()
    {
        while (true)
        {
            if (_fault != null) return false;
            lock (_lock) if (_inflight.Count == 0) return true;
            await _signal.WaitAsync(50);
        }
    }

    async Task<bool> WaitIdleAsync()
    {
        long seq = _c.StatusSeq;
        while (true)
        {
            if (_fault != null) return false;
            var s = _c.Status;
            long now = _c.StatusSeq;
            if (now >= seq + 2 && s?.State == MachineState.Idle) return true;
            if (now >= seq + 1 && s?.State == MachineState.Alarm) { Fault("Controller entered ALARM state.", true); return false; }
            await Task.Delay(30);
        }
    }

    void Fault(string reason, bool reset)
    {
        lock (_lock)
        {
            if (_fault != null) return;
            _fault = reason;
        }
        if (reset)
        {
            _c.FeedHold();
            _c.SoftReset();
        }
        _signal.Release();
    }

    void OnLost(string reason) => Fault($"Connection lost: {reason}", false);
    void OnAlarm(int code, string text) => Fault($"ALARM:{code} ({text})", true);

    static List<Item> Prepare(IReadOnlyList<string> lines, int srcBase)
    {
        var items = new List<Item>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = (lines[i] ?? "").Trim();
            int src = srcBase < 0 ? -1 : i;
            var mo = OpRx().Match(raw);
            if (mo.Success) { items.Add(new Item(src, ItemKind.Op, "", int.Parse(mo.Groups[1].Value, CultureInfo.InvariantCulture))); continue; }
            var mt = ToolRx().Match(raw);
            if (mt.Success) { items.Add(new Item(src, ItemKind.Tool, "", int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture))); continue; }
            var t = Clean(raw);
            if (t.Length > 0) items.Add(new Item(src, ItemKind.Code, t, 0));
        }
        return items;
    }

    /// <summary>Strips ( ) and ; comments and whitespace, upper-cases. "%" delimiter lines become empty.</summary>
    internal static string Clean(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool paren = false;
        foreach (var c in line)
        {
            if (paren) { if (c == ')') paren = false; continue; }
            if (c == '(') { paren = true; continue; }
            if (c == ';') break;
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        var s = sb.ToString();
        return s == "%" ? "" : s;
    }
}
