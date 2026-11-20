using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lathe.Core.Model;
using Lathe.Core.Tools;

namespace Lathe.Core.Project;

/// <summary>Everything needed to make one part: stock, geometry, tools and operations.</summary>
public sealed class LatheProject : ModelBase
{
    public const int CurrentSchema = 1;
    private string _name = "Untitled part", _material = "Aluminium (6061)", _notes = ""; private UnitSystem _units = UnitSystem.Mm;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Name { get => _name; set => Set(ref _name, value); }
    public UnitSystem Units { get => _units; set => Set(ref _units, value); }
    public string Material { get => _material; set => Set(ref _material, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public Stock Stock { get; set; } = new();
    public PartModel Part { get; set; } = new();
    public ObservableCollection<ToolDef> Tools { get; set; } = new();
    public ObservableCollection<OpDef> Operations { get; set; } = new();

    public ToolDef? Tool(int number) => Tools.FirstOrDefault(t => t.Number == number);
}

/// <summary>Machine limits and setup. Lives in %AppData%\LatheStudio\machine.json.</summary>
public sealed class MachineProfile : ModelBase
{
    private string _name = "My lathe"; private int _maxRpm = 2500, _minRpm = 100, _chuckMaxRpm = 3000;
    private double _chuckDia = 100, _maxFeedZ = 1000, _maxFeedX = 800, _accelZ = 100, _clearance = 1.0, _tcX = 20, _tcZ = 20, _tol = 0.002;
    public string Name { get => _name; set => Set(ref _name, value); }
    public int MaxRpm { get => _maxRpm; set => Set(ref _maxRpm, value); }
    public int MinRpm { get => _minRpm; set => Set(ref _minRpm, value); }
    public int ChuckMaxRpm { get => _chuckMaxRpm; set => Set(ref _chuckMaxRpm, value); }
    public double ChuckDiameter { get => _chuckDia; set => Set(ref _chuckDia, value); }
    /// <summary>mm/min (from $112 when connected).</summary>
    public double MaxFeedZ { get => _maxFeedZ; set => Set(ref _maxFeedZ, value); }
    /// <summary>mm/min (from $110 when connected).</summary>
    public double MaxFeedX { get => _maxFeedX; set => Set(ref _maxFeedX, value); }
    /// <summary>Z acceleration mm/s² (from $122) – used for threading lead-in.</summary>
    public double AccelZ { get => _accelZ; set => Set(ref _accelZ, value); }
    /// <summary>Radial clearance kept above material for rapid moves.</summary>
    public double Clearance { get => _clearance; set => Set(ref _clearance, value); }
    /// <summary>Tool-change position: this much above the stock diameter (radial) …</summary>
    public double ToolChangeAboveStock { get => _tcX; set => Set(ref _tcX, value); }
    /// <summary>… and this far right of the stock face.</summary>
    public double ToolChangeRightOfFace { get => _tcZ; set => Set(ref _tcZ, value); }
    /// <summary>Chord tolerance for arcs → line segments.</summary>
    public double Tolerance { get => _tol; set => Set(ref _tol, value); }
    /// <summary>Spindle encoder present: enables G95 feed-per-rev and G33 threading.</summary>
    public bool SpindleSync { get; set; } = true;
    public int SpindleSpinUpSeconds { get; set; } = 2;
    public int EffectiveMaxRpm => Math.Min(MaxRpm, ChuckMaxRpm);
}

public static class AppPaths
{
    public static string Root
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("LATHESTUDIO_HOME");
            if (string.IsNullOrWhiteSpace(p))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LatheStudio");
            Directory.CreateDirectory(p);
            return p;
        }
    }
    public static string MachineFile => Path.Combine(Root, "machine.json");
    public static string ToolLibraryFile => Path.Combine(Root, "tools.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AutosaveFile => Path.Combine(Root, "autosave.lathe");
    public static string LogDir { get { var d = Path.Combine(Root, "logs"); Directory.CreateDirectory(d); return d; } }
}

/// <summary>Atomic, validated JSON persistence for projects, machine profiles and tool libraries.</summary>
public static class Store
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Write to a temp file, keep a .bak of the previous version, then swap in.</summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    public static LatheProject LoadProject(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) { throw new InvalidDataException($"Could not read '{Path.GetFileName(path)}': {e.Message}", e); }
        LatheProject? p;
        try
        {
            using var doc = JsonDocument.Parse(text);
            int schema = doc.RootElement.TryGetProperty("SchemaVersion", out var sv) && sv.TryGetInt32(out var v) ? v : 0;
            if (schema > LatheProject.CurrentSchema)
                throw new InvalidDataException($"This file was made by a newer version (schema {schema}). Please update LatheStudio.");
            text = Migrate(text, schema);
            p = JsonSerializer.Deserialize<LatheProject>(text, Json);
        }
        catch (JsonException e) { throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a valid project file: {e.Message}", e); }
        if (p is null) throw new InvalidDataException("Project file is empty.");
        p.SchemaVersion = LatheProject.CurrentSchema;
        p.Stock ??= new(); p.Part ??= new(); p.Tools ??= new(); p.Operations ??= new();
        p.Part.OdFeatures ??= new(); p.Part.IdFeatures ??= new(); p.Part.Grooves ??= new(); p.Part.Threads ??= new();
        return p;
    }

    /// <summary>Hook for future schema upgrades.</summary>
    private static string Migrate(string json, int fromSchema) => json;

    public static void SaveProject(string path, LatheProject p) => SaveAtomic(path, p);

    /// <summary>Load or fall back to defaults if missing/corrupt (the bad file is kept as .corrupt).</summary>
    public static T LoadOrDefault<T>(string path, Func<T> fallback, Action<string>? warn = null)
    {
        if (!File.Exists(path)) return fallback();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? fallback(); }
        catch (Exception e)
        {
            try { File.Copy(path, path + ".corrupt", true); } catch { }
            warn?.Invoke($"{Path.GetFileName(path)} could not be read ({e.Message}); defaults were used and the file was kept as .corrupt.");
            return fallback();
        }
    }
}
