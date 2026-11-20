using System.Text.Json.Serialization;

namespace Lathe.Core.Tools;

public enum ToolType { Turning, Boring, Grooving, PartOff, ThreadExternal, ThreadInternal, Drill, CenterDrill }
public enum InsertShape { C80, D55, V35, T60, W80, Round }
public enum ToolMaterial { HSS, Carbide }

/// <summary>
/// A lathe tool. Control point (what X/Z positions refer to, and what you touch off with):
///  - Turning: lowest point of the nose (X) and leftmost point of the nose (Z).
///  - Boring: highest point of the nose (X) and leftmost point (Z).
///  - Grooving/part-off: front edge (X) and RIGHT side of the blade (Z).
///  - Threading: the tip. Drills: the point, on the centerline.
/// Offsets are relative to the master tool (the one used to set work zero); X offset is in diameter.
/// </summary>
public sealed class ToolDef : ModelBase
{
    private int _number = 1; private string _name = "Turning tool"; private ToolType _type = ToolType.Turning;
    private InsertShape _shape = InsertShape.C80; private ToolMaterial _material = ToolMaterial.Carbide;
    private double _nose = 0.4, _approach = 95, _size = 9.5, _width = 3, _maxDepth = 12, _dia = 10, _minBore = 12, _point = 118;
    private double _offX, _offZ; private string _notes = "";

    public int Number { get => _number; set => Set(ref _number, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public ToolType Type { get => _type; set => Set(ref _type, value); }
    public InsertShape Shape { get => _shape; set => Set(ref _shape, value); }
    public ToolMaterial Material { get => _material; set => Set(ref _material, value); }
    /// <summary>Nose (corner) radius, mm.</summary>
    public double NoseRadius { get => _nose; set => Set(ref _nose, value); }
    /// <summary>Holder approach angle (95° for most CCMT/CNMG holders, 93° for D/V).</summary>
    public double ApproachAngle { get => _approach; set => Set(ref _approach, value); }
    /// <summary>Insert inscribed-circle size, mm (cutting edge length for modelling).</summary>
    public double InsertSize { get => _size; set => Set(ref _size, value); }
    /// <summary>Blade width for grooving/part-off tools.</summary>
    public double Width { get => _width; set => Set(ref _width, value); }
    /// <summary>Max reach: groove/part depth, boring depth or drill flute length.</summary>
    public double MaxDepth { get => _maxDepth; set => Set(ref _maxDepth, value); }
    /// <summary>Drill diameter or boring bar shank diameter.</summary>
    public double Diameter { get => _dia; set => Set(ref _dia, value); }
    /// <summary>Smallest bore a boring bar/internal threading tool fits into.</summary>
    public double MinBoreDia { get => _minBore; set => Set(ref _minBore, value); }
    /// <summary>Drill point angle.</summary>
    public double PointAngle { get => _point; set => Set(ref _point, value); }
    public double OffsetX { get => _offX; set => Set(ref _offX, value); }
    public double OffsetZ { get => _offZ; set => Set(ref _offZ, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }

    [JsonIgnore] public double IncludedAngle => Shape switch
    {
        InsertShape.C80 => 80, InsertShape.D55 => 55, InsertShape.V35 => 35, InsertShape.T60 => 60, InsertShape.W80 => 80, _ => 90,
    };

    /// <summary>
    /// Steepest angle (below horizontal, moving toward the chuck) this tool can ramp down at without
    /// rubbing its trailing edge, keeping 2° clearance. 95° C-insert ≈ 3°, 93° V-insert ≈ 50°.
    /// </summary>
    [JsonIgnore] public double MaxRampAngle => Type is ToolType.Turning or ToolType.Boring
        ? (Shape == InsertShape.Round ? 60 : Math.Max(0, 180 - ApproachAngle - IncludedAngle - 2))
        : 0;

    [JsonIgnore] public bool IsInside => Type is ToolType.Boring or ToolType.ThreadInternal;
    [JsonIgnore] public bool IsAxial => Type is ToolType.Drill or ToolType.CenterDrill;

    public override string Summary => $"T{Number} {Name}";

    public ToolDef Clone() => (ToolDef)MemberwiseClone();

    public IEnumerable<Core.Issue> Validate()
    {
        string w = Summary;
        if (Number < 1 || Number > 99) yield return Core.Issue.Err("Tool number must be 1-99.", w);
        if (NoseRadius < 0 || NoseRadius > 3) yield return Core.Issue.Err("Nose radius must be 0-3 mm.", w);
        if (Type is ToolType.Turning or ToolType.Boring && (ApproachAngle < 45 || ApproachAngle > 120)) yield return Core.Issue.Err("Approach angle should be 45-120°.", w);
        if (Type is ToolType.Grooving or ToolType.PartOff && (Width <= 0.3 || Width > 12)) yield return Core.Issue.Err("Blade width must be 0.3-12 mm.", w);
        if (IsAxial && Diameter <= 0) yield return Core.Issue.Err("Drill diameter must be positive.", w);
        if (MaxDepth <= 0) yield return Core.Issue.Err("Max depth/reach must be positive.", w);
        if (Type == ToolType.Boring && Diameter >= MinBoreDia) yield return Core.Issue.Err("Boring bar diameter must be smaller than its minimum bore.", w);
    }

    public static ToolDef Turn(int n, string name = "OD turning CCMT 0.4R") => new() { Number = n, Name = name, Type = ToolType.Turning, Shape = InsertShape.C80, ApproachAngle = 95, NoseRadius = 0.4 };
    public static ToolDef Profile(int n) => new() { Number = n, Name = "OD profiling VCMT 0.2R", Type = ToolType.Turning, Shape = InsertShape.V35, ApproachAngle = 93, NoseRadius = 0.2, InsertSize = 9.5 };
    public static ToolDef Bore(int n, double bar = 8, double minBore = 10) => new() { Number = n, Name = $"Boring bar Ø{bar}", Type = ToolType.Boring, Shape = InsertShape.C80, ApproachAngle = 95, NoseRadius = 0.2, Diameter = bar, MinBoreDia = minBore, MaxDepth = 40, InsertSize = 6.35 };
    public static ToolDef Groove(int n, double w = 2) => new() { Number = n, Name = $"Grooving {w} mm", Type = ToolType.Grooving, Width = w, NoseRadius = 0.1, MaxDepth = 6 };
    public static ToolDef Parting(int n, double w = 2) => new() { Number = n, Name = $"Parting blade {w} mm", Type = ToolType.PartOff, Width = w, NoseRadius = 0.1, MaxDepth = 20 };
    public static ToolDef ThreadOd(int n) => new() { Number = n, Name = "OD threading 60°", Type = ToolType.ThreadExternal, NoseRadius = 0.05, MaxDepth = 3 };
    public static ToolDef ThreadId(int n) => new() { Number = n, Name = "ID threading 60°", Type = ToolType.ThreadInternal, NoseRadius = 0.05, Diameter = 8, MinBoreDia = 10, MaxDepth = 25 };
    public static ToolDef DrillOf(int n, double d) => new() { Number = n, Name = $"Drill Ø{d}", Type = ToolType.Drill, Material = ToolMaterial.HSS, Diameter = d, PointAngle = 118, MaxDepth = Math.Max(20, d * 8) };
    public static ToolDef Center(int n) => new() { Number = n, Name = "Center drill #2", Type = ToolType.CenterDrill, Material = ToolMaterial.HSS, Diameter = 2, PointAngle = 118, MaxDepth = 5 };
}
