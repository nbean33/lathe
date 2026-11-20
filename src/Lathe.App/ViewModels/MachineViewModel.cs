using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lathe.App.Infrastructure;
using Lathe.Cam;
using Lathe.Core;
using Lathe.Core.Tools;
using Lathe.Machine;

namespace Lathe.App.ViewModels;

public sealed record CheckItem(bool Ok, bool Blocking, string Text)
{
    public string Icon => Ok ? "✔" : Blocking ? "✖" : "⚠";
}

/// <summary>Connection, DRO, jogging, touch-off, tool changes and running programs.</summary>
public sealed partial class MachineViewModel : ObservableObject, IDisposable
{
    public const string SimPort = "Simulator (virtual lathe)";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly MainViewModel _main;
    private readonly Dispatcher _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private GrblController? _ctl;
    private Streamer? _streamer;
    private CancellationTokenSource? _runCts;
    private DateTime _runStart;
    private DateTime _lastPreflight;
    private bool _airCutActive;

    public MachineViewModel(MainViewModel main) { _main = main; RefreshPorts(); }

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<string> ConsoleLines { get; } = new();
    public ObservableCollection<CheckItem> Checks { get; } = new();
    public ObservableCollection<string> MdiHistory { get; } = new();
    public double[] JogSteps { get; } = { 0.01, 0.1, 1, 10 };

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private bool _connecting;
    [ObservableProperty] private string _stateText = "Disconnected";
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private bool _bannerIsError;
    [ObservableProperty] private double _xDia, _z, _rpm, _feed;
    [ObservableProperty] private string _overrides = "";
    [ObservableProperty] private bool _homed;
    [ObservableProperty] private double _jogStep = 1;
    [ObservableProperty] private double _jogFeed = 300;
    [ObservableProperty] private bool _jogContinuous = true;
    [ObservableProperty] private int _spindleRpm = 500;
    [ObservableProperty] private string _mdiText = "";
    [ObservableProperty] private double _measuredDia = 20;
    [ObservableProperty] private int? _installedTool;
    [ObservableProperty] private double? _masterWcoX, _masterWcoZ;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _runText = "";
    [ObservableProperty] private int _currentLine = -1;
    [ObservableProperty] private int _startOp;
    [ObservableProperty] private bool _airCut;
    [ObservableProperty] private Scene? _scene;
    [ObservableProperty] private string _controllerText = "";

    public ControllerInfo? Info => _ctl?.Info;
    public bool? ControllerSpindleSync => Connected ? Info?.HasSpindleSync : null;
    public bool WorkZeroSet => MasterWcoX is not null && MasterWcoZ is not null;
    public string WorkZeroText => WorkZeroSet ? $"Work zero set (master G54 X{MasterWcoX:0.###} Z{MasterWcoZ:0.###})" : "Work zero NOT set: touch off X and Z.";
    public IEnumerable<ToolDef> Tools => _main.Project.Tools;
    public IReadOnlyList<string> OpNames => _main.Cam?.Ops.Select(o => o.Name).ToList() ?? new List<string>();
    public bool CanRun => Connected && !Running && _main.Cam is { Ok: true } && !_main.OutOfDate && Checks.All(c => c.Ok || !c.Blocking);

    partial void OnMasterWcoXChanged(double? value) { OnPropertyChanged(nameof(WorkZeroSet)); OnPropertyChanged(nameof(WorkZeroText)); RefreshChecks(); }
    partial void OnMasterWcoZChanged(double? value) { OnPropertyChanged(nameof(WorkZeroSet)); OnPropertyChanged(nameof(WorkZeroText)); RefreshChecks(); }
    partial void OnAirCutChanged(bool value) => RefreshChecks();
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnConnectedChanged(bool value) { OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(ControllerSpindleSync)); }

    public void OnProgramChanged()
    {
        OnPropertyChanged(nameof(OpNames)); OnPropertyChanged(nameof(Tools));
        if (StartOp >= OpNames.Count) StartOp = 0;
        RefreshChecks(); BuildScene();
    }

    // ---------------- Connection ----------------

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        Ports.Add(SimPort);
        foreach (var p in SerialTransport.ListPorts()) Ports.Add(p);
        SelectedPort = Ports.Contains(_main.Settings.LastPort ?? "") ? _main.Settings.LastPort : Ports.Count > 1 ? Ports[1] : Ports[0];
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (Connected || Connecting || SelectedPort is null) return;
        Connecting = true; SetBanner("Connecting…", false);
        try
        {
            IMachineTransport tr = SelectedPort == SimPort
                ? new VirtualGrblHal(new VirtualMachineOptions { TimeScale = 4, HomingRequired = true, MaxRpm = Math.Max(3000, _main.Machine.MaxRpm) })
                : new SerialTransport(SelectedPort);
            var ctl = new GrblController(tr);
            ctl.StatusChanged += s => _ui.BeginInvoke(() => OnStatus(s));
            ctl.Console += l => _ui.BeginInvoke(() => AddConsole(l));
            ctl.AlarmRaised += (c, t) => _ui.BeginInvoke(() => { SetBanner($"ALARM {c}: {t}. Fix the cause, then Unlock ($X) or Home.", true); Homed = false; });
            ctl.ErrorRaised += (c, t) => _ui.BeginInvoke(() => AddConsole($"! error {c}: {t}"));
            ctl.ConnectionLost += r => _ui.BeginInvoke(() => OnConnectionLost(r));
            var rep = await ctl.ConnectAsync();
            if (!rep.Ok) { ctl.Dispose(); SetBanner("Could not connect: " + rep.Error, true); return; }
            _ctl = ctl; Connected = true;
            _main.Settings.LastPort = SelectedPort; _main.SaveSettings();
            var i = rep.Info!;
            ControllerText = $"{i.Version}  ·  lathe mode {(i.IsLatheMode ? "on" : "OFF")}  ·  spindle sync {(i.HasSpindleSync ? "yes" : "no")}  ·  RX {i.RxBufferSize}";
            SyncMachineLimits(i);
            SetBanner(rep.Warnings.Count == 0 ? "Connected." : string.Join("  ", rep.Warnings), rep.Warnings.Count > 0);
            MasterWcoX = MasterWcoZ = null; InstalledTool = null;
            if (_main.Cam is not null && _main.Machine.SpindleSync != i.HasSpindleSync) _main.OutOfDate = true;
            RefreshChecks();
        }
        catch (Exception e) { SetBanner("Could not connect: " + e.Message, true); CrashLog.Write(e); }
        finally { Connecting = false; }
    }

    private void SyncMachineLimits(ControllerInfo i)
    {
        double Get(int id, double def) => i.Settings.TryGetValue(id, out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var d) ? d : def;
        var m = _main.Machine;
        m.MaxFeedX = Get(110, m.MaxFeedX); m.MaxFeedZ = Get(112, m.MaxFeedZ); m.AccelZ = Get(122, m.AccelZ);
        m.SpindleSync = i.HasSpindleSync;
        _main.SaveMachine();
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (_ctl is null) return;
        if (Running && MessageBox.Show("A program is running. Disconnecting will stop the machine (feed hold + reset). Continue?", "LatheStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _runCts?.Cancel();
        _ctl.Disconnect(); _ctl.Dispose(); _ctl = null;
        Connected = false; Running = false; StateText = "Disconnected"; Homed = false;
        MasterWcoX = MasterWcoZ = null;
        SetBanner("Disconnected.", false); RefreshChecks();
    }

    private void OnConnectionLost(string reason)
    {
        SetBanner("CONNECTION LOST: " + reason + "  The machine state is unknown. Check the machine, then reconnect and re-home.", true);
        Running = false; Connected = false; StateText = "Connection lost";
        try { _ctl?.Dispose(); } catch { }
        _ctl = null; MasterWcoX = MasterWcoZ = null; Homed = false;
        RefreshChecks();
    }

    private void SetBanner(string text, bool error) { Banner = text; BannerIsError = error; }

    private void AddConsole(string line)
    {
        if (line.StartsWith("<")) return; // status reports are shown in the DRO
        ConsoleLines.Add(line);
        while (ConsoleLines.Count > 400) ConsoleLines.RemoveAt(0);
    }

    private void OnStatus(GrblStatus s)
    {
        StateText = s.State == MachineState.Hold ? "Hold (press Resume)" : s.State.ToString();
        XDia = s.WX; Z = s.WZ; Rpm = s.SpindleRpm; Feed = s.Feed;
        Overrides = $"Feed {s.FeedOv}%  Rapid {s.RapidOv}%  Spindle {s.SpindleOv}%";
        Homed = _ctl?.IsHomed == true;
        if (Running && _streamer is not null)
        {
            Progress = _streamer.TotalLines == 0 ? 0 : 100.0 * _streamer.AckedLines / _streamer.TotalLines;
            CurrentLine = _streamer.CurrentSourceLine;
            var ops = _main.Cam?.Ops;
            int oi = _streamer.CurrentOpIndex;
            RunText = $"{(ops is not null && oi >= 0 && oi < ops.Count ? ops[oi].Name : "")} · line {CurrentLine + 1}/{_main.Post?.Lines.Count} · {DateTime.Now - _runStart:mm\\:ss}";
        }
        // A new Scene instance makes the view redraw with the tool at its live position.
        var tp = new Lathe.Core.Geometry.Pt(s.WZ, s.WX / 2);
        if (Scene is { } sc && sc.ToolPos != tp) Scene = sc with { ToolPos = tp };
        if ((DateTime.Now - _lastPreflight).TotalMilliseconds > 500) RefreshChecks();
    }

    // ---------------- Basic controls ----------------

    private async Task Cmd(string line)
    {
        if (_ctl is null) return;
        var (ok, err) = await _ctl.SendCommandAsync(line);
        if (!ok) SetBanner($"'{line}' failed: {err}", true);
    }

    [RelayCommand] private async Task Home() { if (_ctl is null) return; SetBanner("Homing…", false); var (ok, err) = await _ctl.HomeAsync(); SetBanner(ok ? "Homed." : "Homing failed: " + err, !ok); }
    [RelayCommand] private async Task Unlock() { if (_ctl is null) return; var (ok, err) = await _ctl.UnlockAsync(); SetBanner(ok ? "Unlocked. Position may be unreliable until you home." : "Unlock failed: " + err, !ok); }
    [RelayCommand] private void Reset() { _ctl?.SoftReset(); SetBanner("Controller reset.", false); }
    [RelayCommand] private void FeedHold() { _ctl?.FeedHold(); Paused = true; }
    [RelayCommand] private void Resume() { _ctl?.CycleStart(); Paused = false; }
    [RelayCommand] private async Task SpindleCw() => await Cmd($"M3 S{Math.Clamp(SpindleRpm, 0, _main.Machine.EffectiveMaxRpm)}");
    [RelayCommand] private async Task SpindleStop() => await Cmd("M5");
    [RelayCommand] private async Task CoolantOn() => await Cmd("M8");
    [RelayCommand] private async Task CoolantOff() => await Cmd("M9");
    [RelayCommand] private void FeedOv(string d) => _ctl?.FeedOverride(int.Parse(d, Inv));
    [RelayCommand] private void SpindleOv(string d) => _ctl?.SpindleOverride(int.Parse(d, Inv));

    [RelayCommand]
    private async Task SendMdi()
    {
        var t = MdiText.Trim();
        if (t.Length == 0 || _ctl is null) return;
        if (Running) { SetBanner("MDI is disabled while a program runs.", true); return; }
        MdiHistory.Remove(t); MdiHistory.Insert(0, t);
        MdiText = "";
        await Cmd(t);
    }

    // Jog: step mode sends one move; continuous mode jogs until JogStop (key/button release, focus loss).
    public void JogStart(char axis, int dir)
    {
        if (_ctl is null || Running) return;
        double feed = Math.Clamp(JogFeed, 1, axis == 'X' ? _main.Machine.MaxFeedX : _main.Machine.MaxFeedZ);
        double dist = JogContinuous ? 500 : JogStep;
        if (axis == 'X') dist *= 2; // X is a diameter in lathe mode
        try { _ctl.Jog(axis, dir * dist, feed); } catch (Exception e) { SetBanner(e.Message, true); }
    }
    public void JogStop() { if (JogContinuous) _ctl?.JogCancel(); }

    // ---------------- Work zero & tool offsets ----------------

    private (double x, double z)? CurrentWco()
    {
        var s = _ctl?.Status;
        return s is null ? null : (s.MX - s.WX, s.MZ - s.WZ);
    }
    private ToolDef? Installed => InstalledTool is int n ? _main.Project.Tool(n) : null;

    private async Task<bool> WaitStatus() { await Task.Delay(400); return _ctl?.Status is not null; }

    [RelayCommand]
    private async Task SetZ0()
    {
        if (Installed is not ToolDef t) { SetBanner("Pick the installed tool first.", true); return; }
        await Cmd("G10 L20 P1 Z0");
        if (await WaitStatus() && CurrentWco() is var (_, z)) { MasterWcoZ = z - t.OffsetZ; SetBanner($"Z0 set at the face with {t.Summary}.", false); }
    }

    [RelayCommand]
    private async Task SetX()
    {
        if (Installed is not ToolDef t) { SetBanner("Pick the installed tool first.", true); return; }
        if (MeasuredDia <= 0) { SetBanner("Enter the measured diameter of the skim cut.", true); return; }
        await Cmd(string.Create(Inv, $"G10 L20 P1 X{MeasuredDia:0.####}"));
        if (await WaitStatus() && CurrentWco() is var (x, _)) { MasterWcoX = x - t.OffsetX; SetBanner($"X set to Ø{MeasuredDia:0.###} with {t.Summary}.", false); }
    }

    [RelayCommand]
    private void UseCurrentG54()
    {
        if (Installed is not ToolDef t || CurrentWco() is not var (x, z)) { SetBanner("Connect and pick the installed tool first.", true); return; }
        if (MessageBox.Show("Use the controller's current G54 as work zero for the installed tool? Only do this if you set it yourself for this setup.", "LatheStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        MasterWcoX = x - t.OffsetX; MasterWcoZ = z - t.OffsetZ;
    }

    [RelayCommand]
    private void LearnZ()
    {
        if (Installed is not ToolDef t || MasterWcoZ is not double mz || _ctl?.Status is not { } s) { SetBanner("Set Z0 with another tool first, connect, and pick the installed tool.", true); return; }
        t.OffsetZ = Math.Round(s.MZ - mz, 4);
        SetBanner($"{t.Summary}: Z offset learned = {t.OffsetZ:0.###}. Save the project to keep it.", false);
        _ = ApplyOffsets(t);
    }

    [RelayCommand]
    private void LearnX()
    {
        if (Installed is not ToolDef t || MasterWcoX is not double mx || _ctl?.Status is not { } s || MeasuredDia <= 0) { SetBanner("Set X with another tool first, enter the measured Ø, and pick the installed tool.", true); return; }
        t.OffsetX = Math.Round(s.MX - MeasuredDia - mx, 4);
        SetBanner($"{t.Summary}: X offset learned = {t.OffsetX:0.###} (Ø). Save the project to keep it.", false);
        _ = ApplyOffsets(t);
    }

    private string OffsetLine(ToolDef t, bool air) =>
        string.Create(Inv, $"G10 L2 P1 X{MasterWcoX!.Value + t.OffsetX:0.####} Z{MasterWcoZ!.Value + t.OffsetZ + (air ? 20 : 0):0.####}");

    private async Task ApplyOffsets(ToolDef t) { if (WorkZeroSet) await Cmd(OffsetLine(t, false)); }

    partial void OnInstalledToolChanged(int? value) { if (value is int n && _main.Project.Tool(n) is ToolDef t && !Running) _ = ApplyOffsets(t); BuildScene(); RefreshChecks(); }

    // ---------------- Program ----------------

    public void BuildSceneIfIdle() => BuildScene();

    /// <summary>Copy of the program view plus the live tool position.</summary>
    public void BuildScene()
    {
        var s = _main.ProgramScene ?? _main.PartScene;
        if (s is null) { Scene = null; return; }
        var copy = new Scene
        {
            Stock = s.Stock, Part = s.Part, SimStock = s.SimStock, Chuck = s.Chuck, Path = s.Path, Gouge = s.Gouge, Uncut = s.Uncut,
            Caption = "Live view: the yellow outline is the installed tool at the machine's current position.",
        };
        if (Installed is ToolDef t) { try { copy.ToolShape = ToolShapes.For(t).All; } catch { } }
        if (_ctl?.Status is { } st) copy.ToolPos = new Lathe.Core.Geometry.Pt(st.WZ, st.WX / 2);
        Scene = copy;
    }

    public void RefreshChecks()
    {
        _lastPreflight = DateTime.Now;
        Checks.Clear();
        var cam = _main.Cam; var post = _main.Post;
        if (cam is null || post is null) { Checks.Add(new(false, true, "Generate the program (Program tab).")); OnPropertyChanged(nameof(CanRun)); return; }
        if (_main.OutOfDate) Checks.Add(new(false, true, "Program is out of date: regenerate it."));
        var s = _ctl?.Status; var info = Info;
        double tx = 0, tz = 0;
        bool hasTravel = info is not null && info.Settings.TryGetValue(130, out var a) && double.TryParse(a, NumberStyles.Float, Inv, out tx)
                         & info.Settings.TryGetValue(132, out var b) && double.TryParse(b, NumberStyles.Float, Inv, out tz);
        var wco = CurrentWco();
        bool soft = info?.Settings.TryGetValue(20, out var sl) == true && sl == "1";
        MachineFacts? f = Connected && info is not null && s is not null
            ? new MachineFacts(true, s.State == MachineState.Idle, _ctl!.IsHomed, info.IsLatheMode, info.HasSpindleSync, soft,
                (MasterWcoX ?? wco?.x ?? 0), (MasterWcoZ ?? wco?.z ?? 0) + (AirCut ? 20 : 0), hasTravel ? tx : null, hasTravel ? tz : null, WorkZeroSet)
            : null;
        var issues = Preflight.Check(cam, post, _main.Machine, f);
        if (Installed is null && Connected) issues.Add(Issue.Err("Pick the tool that is installed now."));
        if (issues.Count == 0) Checks.Add(new(true, false, "All checks passed."));
        foreach (var i in issues) Checks.Add(new(false, i.Severity == Severity.Error, i.Message));
        Checks.Add(new(true, false, AirCut ? "Air cut: program runs 20 mm to the right of the part." : "Real cut. Hand near the E-stop, guards closed, chuck key out."));
        OnPropertyChanged(nameof(CanRun));
    }

    [RelayCommand]
    private async Task CheckOnController()
    {
        if (_ctl is null || _main.Post is null || Running) return;
        SetBanner("Checking program on the controller ($C, no motion)…", false);
        var st = _ctl.CreateStreamer();
        string? abort = null; st.Aborted += r => abort = r;
        Running = true;
        try { await st.CheckAsync(_main.Post.Lines); }
        finally { Running = false; }
        var errs = st.Errors;
        SetBanner(abort is null && errs.Count == 0 ? "Controller check passed: no G-code errors." : "Controller check found problems: " + (abort ?? string.Join("; ", errs.Take(5))), !(abort is null && errs.Count == 0));
    }

    [RelayCommand] private Task Run() => RunFrom(0);
    [RelayCommand] private Task RunFromOp() => RunFrom(StartOp);

    private async Task RunFrom(int opIndex)
    {
        RefreshChecks();
        if (!CanRun || _ctl is null || _main.Post is null || _main.Cam is null) { SetBanner("Can't start: see the checklist.", true); return; }
        var cam = _main.Cam; var post = _main.Post; var p = _main.Project;
        var first = cam.Ops[Math.Clamp(opIndex, 0, cam.Ops.Count - 1)];
        string msg = $"Start {(opIndex > 0 ? $"from operation {first.Name}" : "the program")}?\n\n" +
                     $"Part: {p.Name}\nStock: Ø{Units.Format(p.Stock.Diameter, p.Units)} with {Units.Format(p.Stock.StickOut, p.Units)} {Units.Suffix(p.Units)} out of the jaws\n" +
                     $"First tool: {first.Tool.Summary}\nWork zero: {(WorkZeroSet ? "set" : "not set")}\n{(AirCut ? "AIR CUT (+20 mm in Z)\n" : "")}\n" +
                     "Guards closed, chuck key removed, hand near the E-stop.";
        if (MessageBox.Show(msg, "Cycle start", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        _streamer = _ctl.CreateStreamer();
        _streamer.ToolChangeHandler = ToolChange;
        string? aborted = null; bool done = false;
        _streamer.Aborted += r => aborted = r;
        _streamer.Completed += () => done = true;
        _runCts = new CancellationTokenSource();
        _airCutActive = AirCut;
        Running = true; Paused = false; _runStart = DateTime.Now; Progress = 0;
        SetBanner("Running…", false);
        try { await _streamer.RunAsync(post.Lines, opIndex, new[] { post.Preamble }, _runCts.Token); }
        catch (Exception e) { aborted ??= e.Message; CrashLog.Write(e); }
        finally
        {
            Running = false; Paused = false; CurrentLine = -1;
            if (done) { SetBanner($"Program finished in {DateTime.Now - _runStart:mm\\:ss}.", false); Progress = 100; }
            else SetBanner("Program stopped: " + (aborted ?? "unknown reason") + "  Check the machine before continuing.", true);
            if (_airCutActive && Installed is ToolDef t && WorkZeroSet && Connected) { await Task.Delay(300); await ApplyOffsets(t); }
            _airCutActive = false;
            RefreshChecks();
        }
    }

    [RelayCommand] private void Stop() { _streamer?.Stop(); }

    /// <summary>Called by the streamer at each (LS:TOOL n) with the machine idle and the buffer empty.</summary>
    private async Task<IReadOnlyList<string>?> ToolChange(int tool, CancellationToken ct)
    {
        return await _ui.InvokeAsync(() =>
        {
            var t = _main.Project.Tool(tool);
            if (t is null) return (IReadOnlyList<string>?)null;
            if (InstalledTool == tool)
                return WorkZeroSet ? new[] { OffsetLine(t, _airCutActive) } : Array.Empty<string>();
            var choice = ToolChangeDialog.Ask(Application.Current.MainWindow, t, WorkZeroSet);
            if (choice == ToolChangeDialog.Result.Abort) return null;
            InstalledTool = tool;
            BuildScene();
            if (choice == ToolChangeDialog.Result.UseOffsets && WorkZeroSet) return new[] { OffsetLine(t, _airCutActive) };
            return Array.Empty<string>();
        }).Task.Unwrap(ct);
    }

    public void Dispose()
    {
        try { if (Running) _ctl?.FeedHold(); _ctl?.Disconnect(); _ctl?.Dispose(); } catch { }
    }
}

internal static class TaskExt
{
    public static async Task<T> Unwrap<T>(this Task<T> t, CancellationToken ct) => await t.WaitAsync(ct);
}
