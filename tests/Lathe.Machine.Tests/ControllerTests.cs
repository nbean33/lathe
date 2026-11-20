using System.Globalization;
using Lathe.Machine;

namespace Lathe.Machine.Tests;

public class ControllerTests
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    static VirtualMachineOptions Fast(int rx = 1024, double scale = double.PositiveInfinity, bool homing = false) =>
        new() { HomingRequired = homing, TimeScale = scale, RxBufferSize = rx };

    static async Task<(GrblController c, VirtualGrblHal sim, ConnectReport r)> Connect(VirtualMachineOptions? o = null)
    {
        var sim = new VirtualGrblHal(o ?? Fast());
        var c = new GrblController(sim);
        var r = await c.ConnectAsync();
        Assert.True(r.Ok, r.Error);
        return (c, sim, r);
    }

    static async Task WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail("Timed out waiting for condition.");
            await Task.Delay(10);
        }
    }

    /// <summary>Runs the streamer; returns null on Completed, else the abort reason.</summary>
    static async Task<string?> Run(Streamer s, IReadOnlyList<string> prog, int startOp = 0, IReadOnlyList<string>? pre = null)
    {
        string? aborted = null;
        bool completed = false;
        s.Completed += () => completed = true;
        s.Aborted += r => aborted = r;
        await s.RunAsync(prog, startOp, pre);
        return completed ? null : aborted ?? "neither Completed nor Aborted";
    }

    // Work zero: X0 = radius -100 (G10 X is a diameter in G7), Z0 = machine Z -100.
    static List<string> Program(int n = 50)
    {
        var p = new List<string> { "%", "(test part)", "G7 G18 G90 G21 G54 G95", "G10 L2 P1 X-200 Z-100", "M3 S1000", "G0 X40 Z2" };
        for (int i = 0; p.Count < n - 3; i++)
            p.Add(string.Create(CI, $"G1 X{38 - (i % 10) * 0.5:0.###} Z{-0.5 * (i + 1):0.###} F0.1"));
        p.Add("G0 X50 Z5");
        p.Add("M5");
        p.Add("M30");
        return p;
    }

    [Fact]
    public async Task Connect_ReportsLatheModeAndHomingAlarm()
    {
        var (c, _, r) = await Connect(new VirtualMachineOptions { TimeScale = double.PositiveInfinity });
        using var _c = c;
        Assert.NotNull(r.Info);
        Assert.True(r.Info!.IsLatheMode);
        Assert.True(r.Info.HasSpindleSync);
        Assert.Equal(1024, r.Info.RxBufferSize);
        Assert.Equal("2", r.Info.Settings[32]);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("lathe mode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("spindle sync", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Warnings, w => w.Contains("ALARM"));
        Assert.True(c.IsConnected);
        Assert.False(c.IsHomed);
    }

    [Fact]
    public async Task Connect_WarnsWhenNoSpindleSync()
    {
        var (c, _, r) = await Connect(new VirtualMachineOptions { HomingRequired = false, SpindleSync = false });
        using var _c = c;
        Assert.False(r.Info!.HasSpindleSync);
        Assert.Contains(r.Warnings, w => w.Contains("spindle sync", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Connect_FailsCleanlyWhenNothingAnswers()
    {
        using var c = new GrblController(new SilentTransport());
        var r = await c.ConnectAsync();
        Assert.False(r.Ok);
        Assert.Contains("No response", r.Error);
    }

    [Fact]
    public async Task HomeThenMdiMove()
    {
        var (c, sim, _) = await Connect(new VirtualMachineOptions { TimeScale = double.PositiveInfinity });
        using var _c = c;
        var g = await c.SendCommandAsync("G0 X10");
        Assert.False(g.ok);                       // locked until homed
        Assert.Contains("error:9", g.error);

        var h = await c.HomeAsync();
        Assert.True(h.ok, h.error);
        Assert.True(c.IsHomed);
        Assert.True((await c.SendCommandAsync("G7 G90 G21")).ok);
        Assert.True((await c.SendCommandAsync("G10 L2 P1 X-200 Z-100")).ok);
        Assert.True((await c.SendCommandAsync("G0 X20 Z2")).ok);
        await WaitUntil(() => sim.State == MachineState.Idle && Math.Abs(sim.MX + 90) < 1e-6);
        Assert.Equal(-98, sim.MZ, 6);
        await WaitUntil(() => c.Status is { State: MachineState.Idle } s && Math.Abs(s.WX - 20) < 1e-6 && Math.Abs(s.WZ - 2) < 1e-6);
    }

    [Fact]
    public async Task StreamsProgramToCompletion()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var s = c.CreateStreamer();
        var prog = Program();
        var reason = await Run(s, prog);
        Assert.Null(reason);
        Assert.Equal(prog.Count(l => Streamer.Clean(l).Length > 0), s.TotalLines);
        Assert.Equal(s.TotalLines, s.AckedLines);
        Assert.Equal(prog.Count - 1, s.CurrentSourceLine);
        Assert.Equal(-75, sim.MX, 6);  // X50 dia -> radius 25 -> -100 + 25
        Assert.Equal(-95, sim.MZ, 6);
        Assert.False(sim.SpindleOn);
        Assert.Equal(1, sim.ProgramEndCount);
        Assert.False(c.IsStreaming);
    }

    [Fact]
    public async Task FlowControlNeverOverflowsRxBuffer()
    {
        var (c, sim, r) = await Connect(Fast(rx: 128, scale: 20));
        using var _c = c;
        Assert.Equal(128, r.Info!.RxBufferSize);
        var reason = await Run(c.CreateStreamer(), Program());
        Assert.Null(reason);
        Assert.False(sim.RxOverflow);
        Assert.InRange(sim.MaxRxBytes, 32, 128);
    }

    [Fact]
    public async Task ErrorMidProgramAbortsAndStopsSending()
    {
        var (c, sim, _) = await Connect(Fast(rx: 128));
        using var _c = c;
        var prog = Program();
        prog[10] = "G5 X1";
        var reason = await Run(c.CreateStreamer(), prog);
        Assert.NotNull(reason);
        Assert.Contains("error:20", reason);
        Assert.Contains("line 11", reason);
        var late = Streamer.Clean(prog[^5]);
        Assert.DoesNotContain(late, sim.ReceivedLines);
        Assert.DoesNotContain(late, sim.ExecutedLines);
        await WaitUntil(() => sim.State == MachineState.Idle);
        Assert.False(sim.SpindleOn);
    }

    [Fact]
    public async Task ToolChangeHandlerRunsWhenIdleAndItsLinesAreSent()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var prog = new List<string>
        {
            "(LS:OP 1 Face)", "G7 G90 G21 G94", "G10 L2 P1 X-200 Z-100", "G0 X40 Z2", "G1 Z0 F100",
            "(LS:TOOL 2)",
            "(LS:OP 2 Turn)", "G0 X30 Z2", "G1 Z-10 F100", "M30",
        };
        int calledWith = 0;
        MachineState stateAtCall = MachineState.Unknown;
        var s = c.CreateStreamer();
        s.ToolChangeHandler = (t, ct) =>
        {
            calledWith = t;
            stateAtCall = sim.State;
            return Task.FromResult<IReadOnlyList<string>?>(["G10 L2 P1 X-198 Z-100 (T2 offset)"]);
        };
        Assert.Null(await Run(s, prog));
        Assert.Equal(2, calledWith);
        Assert.Equal(MachineState.Idle, stateAtCall);
        var rx = sim.ReceivedLines.ToList();
        int g10 = rx.IndexOf("G10L2P1X-198Z-100");
        Assert.True(g10 > rx.IndexOf("G1Z0F100"));
        Assert.True(g10 < rx.IndexOf("G0X30Z2"));
        Assert.Equal(-99, sim.WcoX, 6);
        Assert.Equal(2, s.CurrentOpIndex);
    }

    [Fact]
    public async Task ToolChangeCancelledAborts()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var s = c.CreateStreamer();
        s.ToolChangeHandler = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        var reason = await Run(s, ["G0 Z-1", "(LS:TOOL 3)", "G0 Z-2"]);
        Assert.Contains("cancelled", reason);
        Assert.DoesNotContain("G0Z-2", sim.ReceivedLines);
    }

    [Fact]
    public async Task StartFromOperationSkipsEarlierOps()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var prog = new List<string>
        {
            "(LS:OP 1 Face)", "G7 G90 G21 G94", "G0 X40 Z2", "G1 Z0 F100",
            "(LS:OP 2 Turn)", "G0 X30 Z2", "G1 Z-10 F100", "M30",
        };
        var reason = await Run(c.CreateStreamer(), prog, startOp: 2, pre: ["G7 G90 G21 G94", "G10 L2 P1 X-200 Z-100"]);
        Assert.Null(reason);
        var rx = sim.ReceivedLines.ToList();
        Assert.DoesNotContain("G0X40Z2", rx);
        Assert.DoesNotContain("G1Z0F100", rx);
        Assert.True(rx.IndexOf("G10L2P1X-200Z-100") < rx.IndexOf("G0X30Z2"));
        Assert.Equal(-110, sim.MZ, 6);
    }

    [Fact]
    public async Task StartFromMissingOperationAbortsWithoutSending()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        int before = sim.ReceivedLines.Count;
        var reason = await Run(c.CreateStreamer(), ["(LS:OP 1 A)", "G0 Z-1"], startOp: 5);
        Assert.Contains("not found", reason);
        Assert.Equal(before, sim.ReceivedLines.Count);
    }

    [Fact]
    public async Task OverlongLineAbortsBeforeSendingAnything()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        int before = sim.ReceivedLines.Count;
        var reason = await Run(c.CreateStreamer(), ["G0 Z-1", "G1 Z-2 F100 " + new string('X', 0) + "(x)" + "X1" + new string('1', 300)]);
        Assert.Contains("too long", reason);
        Assert.Equal(before, sim.ReceivedLines.Count);
    }

    [Fact]
    public async Task SoftLimitAlarmAborts()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var reason = await Run(c.CreateStreamer(), ["G7 G90 G21", "G0 Z-1000", "G0 Z-1"]);
        Assert.NotNull(reason);
        Assert.Contains("ALARM:2", reason);
        Assert.Equal(MachineState.Alarm, sim.State);
        Assert.False(c.IsHomed);
        Assert.True((await c.UnlockAsync()).ok);
        await WaitUntil(() => sim.State == MachineState.Idle);
    }

    [Fact]
    public async Task StopHoldsThenResetsWithoutLosingPosition()
    {
        var (c, sim, _) = await Connect(Fast(scale: 1));
        using var _c = c;
        var s = c.CreateStreamer();
        string? aborted = null;
        s.Aborted += r => aborted = r;
        var run = s.RunAsync(["G7 G90 G21 G94", "G1 Z-100 F60", "G1 Z-200 F60"]);
        await WaitUntil(() => sim.State == MachineState.Run && sim.MZ < -0.05);
        s.Stop();
        await run;
        Assert.Equal("stopped by user", aborted);
        await WaitUntil(() => sim.State == MachineState.Idle);   // hold first => no ALARM:3
        double z = sim.MZ;
        await Task.Delay(200);
        Assert.Equal(z, sim.MZ, 9);
        Assert.False(c.IsStreaming);
        Assert.True((await c.SendCommandAsync("G0 Z-1")).ok);  // controller is usable again
    }

    [Fact]
    public async Task M0PausesUntilCycleStart()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var s = c.CreateStreamer();
        bool completed = false;
        s.Completed += () => completed = true;
        var run = s.RunAsync(["G7 G90 G21", "G0 Z-1", "M0", "G0 Z-2"]);
        await WaitUntil(() => sim.State == MachineState.Hold);
        await Task.Delay(100);
        Assert.False(completed);
        c.CycleStart();
        await run;
        Assert.True(completed);
        Assert.Equal(-2, sim.MZ, 6);
    }

    [Fact]
    public async Task MdiRefusedWhileStreaming()
    {
        var (c, _, _) = await Connect(Fast(scale: 1));
        using var _c = c;
        var s = c.CreateStreamer();
        var run = s.RunAsync(["G7 G90 G21 G94", "G1 Z-20 F600"]);
        await WaitUntil(() => c.IsStreaming);
        var r = await c.SendCommandAsync("G0 Z0");
        Assert.False(r.ok);
        Assert.Contains("program is running", r.error);
        s.Stop();
        await run;
    }

    [Fact]
    public async Task WatchdogFiresWhenControllerStopsAnswering()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        string? lost = null;
        c.ConnectionLost += r => lost = r;
        sim.Dispose();
        await WaitUntil(() => lost != null, 3000);
        Assert.False(c.IsConnected);
        Assert.Contains("No response", lost);
    }

    [Fact]
    public async Task WatchdogAbortsRunningProgram()
    {
        var (c, sim, _) = await Connect(Fast(scale: 1));
        using var _c = c;
        var s = c.CreateStreamer();
        string? aborted = null;
        s.Aborted += r => aborted = r;
        var run = s.RunAsync(["G7 G90 G21 G94", "G1 Z-100 F60"]);
        await WaitUntil(() => sim.State == MachineState.Run);
        sim.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Connection lost", aborted);
    }

    [Fact]
    public async Task JogAndJogCancel()
    {
        var (c, sim, _) = await Connect(Fast(scale: 1));
        using var _c = c;
        c.Jog('Z', -50, 600);
        await WaitUntil(() => sim.State == MachineState.Jog);
        await Task.Delay(200);
        c.JogCancel();
        await WaitUntil(() => sim.State == MachineState.Idle, 2000);
        Assert.InRange(sim.MZ, -49.0, -0.5);
    }

    [Fact]
    public async Task JogBeyondTravelIsRejected()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        int? err = null;
        c.ErrorRaised += (code, _) => err = code;
        c.Jog('Z', 10, 600); // machine Z max is 0
        await WaitUntil(() => err != null, 2000);
        Assert.Equal(15, err);
        Assert.Equal(0, sim.MZ, 9);
    }

    [Fact]
    public async Task CheckModeReportsErrorsWithoutMotion()
    {
        var (c, sim, _) = await Connect();
        using var _c = c;
        var good = c.CreateStreamer();
        Assert.Null(await Run(good, Program()));  // RunAsync path sanity
        double z = sim.MZ;

        var check = c.CreateStreamer();
        string? aborted = null; bool completed = false;
        check.Aborted += r => aborted = r;
        check.Completed += () => completed = true;
        var bad = Program();
        bad[12] = "G5";
        await check.CheckAsync(bad);
        Assert.False(completed);
        Assert.Contains("error:20", aborted);
        Assert.Single(check.Errors);
        Assert.False(sim.CheckMode);
        Assert.Equal(z, sim.MZ, 9);

        var ok = c.CreateStreamer();
        completed = false;
        ok.Completed += () => completed = true;
        await ok.CheckAsync(Program());
        Assert.True(completed);
        Assert.True((await c.SendCommandAsync("G0 Z-1")).ok); // usable after the check-mode reset
    }

    [Fact]
    public async Task ThreadingRequiresSpindle()
    {
        var (c, _, _) = await Connect();
        using var _c = c;
        var r = await c.SendCommandAsync("G33 Z-5 K1.5");
        Assert.False(r.ok);
        Assert.Contains("error:41", r.error);
        Assert.True((await c.SendCommandAsync("M3 S500")).ok);
        Assert.True((await c.SendCommandAsync("G33 Z-5 K1.5")).ok);
    }

    [Fact]
    public async Task ArcWithBadCenterIsRejected()
    {
        var (c, _, _) = await Connect();
        using var _c = c;
        Assert.True((await c.SendCommandAsync("G7 G90 G21 G94 G10 L2 P1 X-200 Z-100")).ok);
        Assert.True((await c.SendCommandAsync("G0 X20 Z0")).ok);
        Assert.True((await c.SendCommandAsync("G3 X30 Z-5 I0 K-5 F100")).ok);   // r=5 quarter arc (I is radius)
        var bad = await c.SendCommandAsync("G2 X40 Z-20 I0 K-1 F100");
        Assert.False(bad.ok);
        Assert.Contains("error:33", bad.error);
    }

    sealed class SilentTransport : IMachineTransport
    {
        public bool IsOpen { get; private set; }
        public event Action<string>? LineReceived { add { } remove { } }
        public event Action<Exception>? Faulted { add { } remove { } }
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void WriteLine(string line) { }
        public void WriteRealtime(byte b) { }
        public void Dispose() => Close();
    }
}
