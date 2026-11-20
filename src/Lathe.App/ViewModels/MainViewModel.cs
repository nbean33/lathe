using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Clipper2Lib;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lathe.App.Infrastructure;
using Lathe.Cam;
using Lathe.Core;
using Lathe.Core.Materials;
using Lathe.Core.Model;
using Lathe.Core.Project;
using Lathe.Core.Tools;
using Microsoft.Win32;

namespace Lathe.App.ViewModels;

public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = new();
    public string? LastPort { get; set; }
    public string? LastFolder { get; set; }
    public bool FirstRunDone { get; set; }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ChangeTracker _tracker = new(TimeSpan.FromMilliseconds(250));
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(60) };
    private int _genVersion;

    [ObservableProperty] private LatheProject _project = Templates.New();
    [ObservableProperty] private MachineProfile _machine;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private UnitSystem _units = UnitSystem.Mm;

    // Part tab
    [ObservableProperty] private Feature? _selectedOd;
    [ObservableProperty] private Feature? _selectedId;
    [ObservableProperty] private Groove? _selectedGroove;
    [ObservableProperty] private ThreadFeature? _selectedThread;
    [ObservableProperty] private Scene? _partScene;
    public ObservableCollection<Issue> PartIssues { get; } = new();

    // Tools tab
    [ObservableProperty] private ToolDef? _selectedTool;
    [ObservableProperty] private Scene? _toolScene;

    // Program tab
    [ObservableProperty] private OpDef? _selectedOp;
    [ObservableProperty] private Scene? _programScene;
    [ObservableProperty] private string _gcodeText = "";
    [ObservableProperty] private bool _outOfDate = true;
    [ObservableProperty] private bool _generating;
    [ObservableProperty] private string _programSummary = "Not generated yet.";
    [ObservableProperty] private int _scrubOp = -1;
    public ObservableCollection<Issue> ProgramIssues { get; } = new();
    public CamResult? Cam { get; private set; }
    public PostResult? Post { get; private set; }

    public AppSettings Settings { get; }
    public MachineViewModel MachineVm { get; }
    public IReadOnlyList<string> MaterialNames { get; } = MaterialTable.All.Select(m => m.Name).ToList();
    public IReadOnlyList<(string Name, Func<LatheProject> Make)> TemplateList => Templates.All;
    public string Title => $"LatheStudio – {Project.Name}{(Dirty ? " *" : "")}";
    public event Action<string, string>? Notify; // (title, message)

    public MainViewModel()
    {
        Settings = Store.LoadOrDefault(AppPaths.SettingsFile, () => new AppSettings(), w => PendingWarnings.Add(w));
        _machine = Store.LoadOrDefault(AppPaths.MachineFile, () => new MachineProfile(), w => PendingWarnings.Add(w));
        MachineVm = new MachineViewModel(this);
        _tracker.Changed += OnModelChanged;
        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();
        Load(Templates.New(), null);
    }

    public List<string> PendingWarnings { get; } = new();

    partial void OnDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    partial void OnUnitsChanged(UnitSystem value) { if (Project.Units != value) Project.Units = value; }
    partial void OnScrubOpChanged(int value) => BuildProgramScene();
    partial void OnSelectedToolChanged(ToolDef? value) => BuildToolScene();
    partial void OnSelectedOpChanged(OpDef? value) => BuildProgramScene();

    public void Load(LatheProject p, string? path)
    {
        Project = p; FilePath = path; Units = p.Units;
        _tracker.Attach(Project);
        SelectedOd = p.Part.OdFeatures.FirstOrDefault(); SelectedId = p.Part.IdFeatures.FirstOrDefault();
        SelectedGroove = p.Part.Grooves.FirstOrDefault(); SelectedThread = p.Part.Threads.FirstOrDefault();
        SelectedTool = p.Tools.FirstOrDefault(); SelectedOp = p.Operations.FirstOrDefault();
        Cam = null; Post = null; GcodeText = ""; OutOfDate = true; ProgramSummary = "Not generated yet."; ProgramIssues.Clear();
        RefreshPart();
        Dirty = false;
        OnPropertyChanged(nameof(Title));
        MachineVm.OnProgramChanged();
    }

    private void OnModelChanged()
    {
        Dirty = true;
        if (!OutOfDate) { OutOfDate = true; ProgramSummary = "The part, tools or operations changed: regenerate before running."; MachineVm.OnProgramChanged(); }
        RefreshPart();
        BuildToolScene();
        OnPropertyChanged(nameof(Title));
    }

    // ---------------- Part ----------------

    public void RefreshPart()
    {
        PartGeometry g;
        try { g = PartBuilder.Build(Project.Stock, Project.Part); }
        catch (Exception e) { PartIssues.Clear(); PartIssues.Add(Issue.Err("Geometry error: " + e.Message)); return; }
        PartIssues.Clear();
        foreach (var i in g.Issues) PartIssues.Add(i);
        if (g.Issues.Count == 0) PartIssues.Add(Issue.Note($"Part OK: length {Lathe.Core.Units.Format(g.Length, Units)} {Lathe.Core.Units.Suffix(Units)}, max Ø{Lathe.Core.Units.Format(2 * (g.Od.IsEmpty ? 0 : g.Od.ToPolyline(0.05).Max(p => p.R)), Units)}."));
        PartScene = MakeBaseScene(g, caption: "Part (blue) inside the stock (grey). The chuck is on the left.");
    }

    private Scene MakeBaseScene(PartGeometry g, string? caption = null, PathsD? sim = null, List<(MoveKind, Lathe.Core.Geometry.Pt, Lathe.Core.Geometry.Pt, int)>? path = null, int hi = -1, PathsD? gouge = null, PathsD? uncut = null)
    {
        PathsD? part = null;
        try
        {
            var ctx = new CamContext(Project, Machine, g);
            ctx.Complete();
            part = ctx.PartUpper;
        }
        catch { }
        return new Scene
        {
            Stock = Geo.One(Geo.Rect(g.JawZ, g.StockFaceZ, g.StockBoreR, g.StockR)),
            Part = part, SimStock = sim, Gouge = gouge, Uncut = uncut,
            Chuck = (g.JawZ, Math.Max(Machine.ChuckDiameter / 2, g.StockR + 10), g.StockR),
            Path = path ?? new(), HighlightOp = hi, Caption = caption,
        };
    }

    [RelayCommand] private void AddCylinder() => AddOd(new CylinderFeature { Diameter = LastOdDia(), Length = 10 });
    [RelayCommand] private void AddTaper() => AddOd(new TaperFeature { StartDiameter = LastOdDia(), EndDiameter = LastOdDia(), Length = 10 });
    [RelayCommand] private void AddArc() => AddOd(new ArcFeature { EndDiameter = LastOdDia(), Length = 5, Radius = 5 });
    [RelayCommand] private void AddBore() { var f = new CylinderFeature { Diameter = 10, Length = 10 }; Project.Part.IdFeatures.Add(f); SelectedId = f; }
    [RelayCommand] private void AddBoreTaper() { var f = new TaperFeature { StartDiameter = 12, EndDiameter = 10, Length = 10 }; Project.Part.IdFeatures.Add(f); SelectedId = f; }
    [RelayCommand] private void AddGroove() { var g = new Groove { Position = 10, Width = 2, Diameter = Math.Max(1, LastOdDia() - 2) }; Project.Part.Grooves.Add(g); SelectedGroove = g; }
    [RelayCommand] private void AddThread() { var t = new ThreadFeature { Spec = "M10x1.5", Length = 10 }; Project.Part.Threads.Add(t); SelectedThread = t; }

    private void AddOd(Feature f) { int i = SelectedOd is null ? -1 : Project.Part.OdFeatures.IndexOf(SelectedOd); Project.Part.OdFeatures.Insert(i + 1, f); SelectedOd = f; }
    private double LastOdDia() => SelectedOd switch
    {
        CylinderFeature c => c.Diameter, TaperFeature t => t.EndDiameter, ArcFeature a => a.EndDiameter,
        _ => Math.Max(1, Project.Stock.Diameter - 2),
    };

    [RelayCommand] private void RemoveOd() => Remove(Project.Part.OdFeatures, SelectedOd, x => SelectedOd = x);
    [RelayCommand] private void RemoveId() => Remove(Project.Part.IdFeatures, SelectedId, x => SelectedId = x);
    [RelayCommand] private void RemoveGroove() => Remove(Project.Part.Grooves, SelectedGroove, x => SelectedGroove = x);
    [RelayCommand] private void RemoveThread() => Remove(Project.Part.Threads, SelectedThread, x => SelectedThread = x);
    [RelayCommand] private void OdUp() => Move(Project.Part.OdFeatures, SelectedOd, -1);
    [RelayCommand] private void OdDown() => Move(Project.Part.OdFeatures, SelectedOd, 1);
    [RelayCommand] private void IdUp() => Move(Project.Part.IdFeatures, SelectedId, -1);
    [RelayCommand] private void IdDown() => Move(Project.Part.IdFeatures, SelectedId, 1);

    private static void Remove<T>(ObservableCollection<T> list, T? item, Action<T?> select) where T : class
    {
        if (item is null) return;
        int i = list.IndexOf(item);
        list.Remove(item);
        select(list.Count == 0 ? null : list[Math.Clamp(i, 0, list.Count - 1)]);
    }
    private static void Move<T>(ObservableCollection<T> list, T? item, int d) where T : class
    {
        if (item is null) return;
        int i = list.IndexOf(item), j = i + d;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.Move(i, j);
    }

    // ---------------- Tools ----------------

    private int NextTool() => Project.Tools.Count == 0 ? 1 : Project.Tools.Max(t => t.Number) + 1;
    [RelayCommand] private void AddTool(string kind)
    {
        int n = NextTool();
        ToolDef t = kind switch
        {
            "profile" => ToolDef.Profile(n), "bore" => ToolDef.Bore(n), "groove" => ToolDef.Groove(n), "part" => ToolDef.Parting(n),
            "threadod" => ToolDef.ThreadOd(n), "threadid" => ToolDef.ThreadId(n), "drill" => ToolDef.DrillOf(n, 10), "center" => ToolDef.Center(n),
            _ => ToolDef.Turn(n),
        };
        Project.Tools.Add(t); SelectedTool = t;
    }
    [RelayCommand] private void RemoveTool()
    {
        if (SelectedTool is null) return;
        if (Project.Operations.Any(o => o.ToolNumber == SelectedTool.Number))
        { Notify?.Invoke("Tool in use", $"{SelectedTool.Summary} is used by an operation. Change those operations first."); return; }
        Remove(Project.Tools, SelectedTool, x => SelectedTool = x);
    }

    private void BuildToolScene()
    {
        var t = SelectedTool;
        if (t is null) { ToolScene = null; return; }
        try
        {
            var sh = ToolShapes.For(t);
            ToolScene = new Scene
            {
                Path = new() { (MoveKind.Rapid, new(-3, 0), new(3, 0), -1), (MoveKind.Rapid, new(0, -3), new(0, 3), -1) },
                Part = null, Stock = new PathsD { sh.Holder }, SimStock = new PathsD { sh.Cutting },
                Caption = "Tool outline. The cross marks the control point (what you touch off with).",
            };
        }
        catch { ToolScene = null; }
    }

    // ---------------- Operations / CAM ----------------

    [RelayCommand] private void AddOp(string kind)
    {
        int Tool(params ToolType[] types) => Project.Tools.FirstOrDefault(t => types.Contains(t.Type))?.Number ?? (Project.Tools.FirstOrDefault()?.Number ?? 1);
        OpDef op = kind switch
        {
            "face" => new FaceOp { ToolNumber = Tool(ToolType.Turning) },
            "odrough" => new TurnOp { ToolNumber = Tool(ToolType.Turning) },
            "odfinish" => new TurnOp { Stage = TurnStage.Finish, ToolNumber = Tool(ToolType.Turning) },
            "idrough" => new TurnOp { Side = TurnSide.Inside, DepthOfCut = 0.5, ToolNumber = Tool(ToolType.Boring) },
            "idfinish" => new TurnOp { Side = TurnSide.Inside, Stage = TurnStage.Finish, ToolNumber = Tool(ToolType.Boring) },
            "drill" => new DrillOp { ToolNumber = Tool(ToolType.Drill) },
            "center" => new DrillOp { ToolNumber = Tool(ToolType.CenterDrill), Depth = 2, PeckDepth = 0 },
            "groove" => new GrooveOp { ToolNumber = Tool(ToolType.Grooving, ToolType.PartOff) },
            "thread" => new ThreadOp { ToolNumber = Tool(ToolType.ThreadExternal) },
            "threadid" => new ThreadOp { Internal = true, ToolNumber = Tool(ToolType.ThreadInternal) },
            _ => new PartOffOp { ToolNumber = Tool(ToolType.PartOff, ToolType.Grooving) },
        };
        int i = op is PartOffOp ? Project.Operations.Count - 1 : (SelectedOp is null ? Project.Operations.Count - 1 : Project.Operations.IndexOf(SelectedOp));
        if (op is not PartOffOp && Project.Operations.LastOrDefault() is PartOffOp && i >= Project.Operations.Count - 1) i = Project.Operations.Count - 2;
        Project.Operations.Insert(Math.Clamp(i + 1, 0, Project.Operations.Count), op);
        SelectedOp = op;
    }
    [RelayCommand] private void RemoveOp() => Remove(Project.Operations, SelectedOp, x => SelectedOp = x);
    [RelayCommand] private void OpUp() => Move(Project.Operations, SelectedOp, -1);
    [RelayCommand] private void OpDown() => Move(Project.Operations, SelectedOp, 1);

    [RelayCommand]
    public async Task Generate()
    {
        if (Generating) return;
        Generating = true;
        int version = ++_genVersion;
        ProgramSummary = "Generating and simulating…";
        try
        {
            var json = JsonSerializer.Serialize(Project, Store.Json);
            var copy = JsonSerializer.Deserialize<LatheProject>(json, Store.Json)!;
            var machine = Machine;
            bool? sync = MachineVm.ControllerSpindleSync;
            var (cam, post) = await Task.Run(() =>
            {
                var c = CamEngine.Generate(copy, machine, new CamOptions { SpindleSync = sync });
                var p = c.Ops.Count > 0 ? GrblHalPost.Build(c, copy, machine) : null;
                return (c, p);
            });
            if (version != _genVersion) return;
            Cam = cam; Post = post;
            ProgramIssues.Clear();
            foreach (var i in cam.AllIssues.OrderByDescending(i => i.Severity)) ProgramIssues.Add(i);
            GcodeText = post?.Text ?? "";
            OutOfDate = false;
            int errors = ProgramIssues.Count(i => i.Severity == Severity.Error), warns = ProgramIssues.Count(i => i.Severity == Severity.Warning);
            ProgramSummary = cam.Ok
                ? $"Ready: {cam.Ops.Count} operations, {post?.Lines.Count ?? 0} lines, about {TimeSpan.FromSeconds(cam.TotalSeconds):m\\:ss} min. {warns} warning(s)."
                : $"Not runnable: {errors} error(s), {warns} warning(s). See the list below.";
            ScrubOp = -1;
            BuildProgramScene();
        }
        catch (Exception e)
        {
            ProgramIssues.Clear(); ProgramIssues.Add(Issue.Err("Generation crashed: " + e.Message));
            ProgramSummary = "Generation failed.";
            CrashLog.Write(e);
        }
        finally { Generating = false; MachineVm.OnProgramChanged(); }
    }

    public void BuildProgramScene()
    {
        var cam = Cam;
        if (cam is null) { ProgramScene = PartScene; return; }
        var path = new List<(MoveKind, Lathe.Core.Geometry.Pt, Lathe.Core.Geometry.Pt, int)>();
        for (int k = 0; k < cam.Ops.Count; k++)
        {
            if (ScrubOp >= 0 && k > ScrubOp) break;
            var op = cam.Ops[k]; var pos = op.Start;
            foreach (var m in op.Moves) { path.Add((m.Kind, pos, m.To, k)); pos = m.To; }
        }
        int hi = SelectedOp is null ? -1 : cam.Ops.FindIndex(o => o.OpIndex == Project.Operations.IndexOf(SelectedOp));
        PathsD? sim = cam.Sim is null ? null : ScrubOp >= 0 && ScrubOp < cam.Sim.StockAfterOp.Count ? cam.Sim.StockAfterOp[ScrubOp] : cam.Sim.FinalStock;
        ProgramScene = MakeBaseScene(cam.Geometry, "Toolpath: blue = cut, dashed red = rapid, purple = thread. Orange = simulated stock.", sim, path, hi,
            cam.Sim?.Gouges, cam.Sim?.Uncut);
    }

    [RelayCommand]
    private void SaveGcode()
    {
        if (Post is null || Cam is null) { Notify?.Invoke("Nothing to save", "Generate the program first."); return; }
        if (!Cam.Ok && MessageBox.Show("The program has errors. Save it anyway (for inspection only)?", "LatheStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dlg = new SaveFileDialog { Filter = "G-code (*.nc)|*.nc|All files|*.*", FileName = SafeName(Project.Name) + ".nc", InitialDirectory = Settings.LastFolder ?? "" };
        if (dlg.ShowDialog() != true) return;
        // The (LS:...) directives are harmless comments to other senders.
        File.WriteAllText(dlg.FileName, Post.Text);
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------------- Files ----------------

    public bool ConfirmDiscard()
    {
        if (!Dirty) return true;
        var r = MessageBox.Show($"Save changes to '{Project.Name}'?", "LatheStudio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveInternal(false);
        return true;
    }

    [RelayCommand] private void New() { if (ConfirmDiscard()) Load(Templates.New(), null); }
    [RelayCommand] private void NewFromTemplate(string name)
    {
        if (!ConfirmDiscard()) return;
        var t = Templates.All.FirstOrDefault(x => x.Name == name);
        if (t.Make is not null) Load(t.Make(), null);
    }

    [RelayCommand]
    private void Open()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "LatheStudio project (*.lathe)|*.lathe|All files|*.*", InitialDirectory = Settings.LastFolder ?? "" };
        if (dlg.ShowDialog() == true) OpenPath(dlg.FileName);
    }

    public void OpenPath(string path)
    {
        try
        {
            var p = Store.LoadProject(path);
            Load(p, path);
            AddRecent(path);
        }
        catch (Exception e) { Notify?.Invoke("Could not open project", e.Message); }
    }

    [RelayCommand] private void Save() => SaveInternal(false);
    [RelayCommand] private void SaveAs() => SaveInternal(true);

    private bool SaveInternal(bool ask)
    {
        string? path = FilePath;
        if (ask || path is null)
        {
            var dlg = new SaveFileDialog { Filter = "LatheStudio project (*.lathe)|*.lathe", FileName = SafeName(Project.Name) + ".lathe", InitialDirectory = Settings.LastFolder ?? "" };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;
        }
        try
        {
            Store.SaveProject(path, Project);
            FilePath = path; Dirty = false; AddRecent(path);
            try { if (File.Exists(AppPaths.AutosaveFile)) File.Delete(AppPaths.AutosaveFile); } catch { }
            return true;
        }
        catch (Exception e) { Notify?.Invoke("Save failed", e.Message); return false; }
    }

    private void AddRecent(string path)
    {
        Settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Settings.RecentFiles.Insert(0, path);
        if (Settings.RecentFiles.Count > 8) Settings.RecentFiles.RemoveRange(8, Settings.RecentFiles.Count - 8);
        Settings.LastFolder = Path.GetDirectoryName(path);
        SaveSettings();
        OnPropertyChanged(nameof(Settings));
    }

    public void SaveSettings() { try { Store.SaveAtomic(AppPaths.SettingsFile, Settings); } catch (Exception e) { CrashLog.Write(e); } }
    public void SaveMachine() { try { Store.SaveAtomic(AppPaths.MachineFile, Machine); } catch (Exception e) { Notify?.Invoke("Could not save machine settings", e.Message); } }

    private void Autosave()
    {
        if (!Dirty) return;
        try { Store.SaveAtomic(AppPaths.AutosaveFile, Project); } catch (Exception e) { CrashLog.Write(e); }
    }

    public void Dispose() { _autosave.Stop(); _tracker.Dispose(); MachineVm.Dispose(); }
}
