using System.Text.Json.Serialization;

namespace Lathe.Core.Model;

public enum EdgeKind { None, Chamfer, Radius }

/// <summary>A corner break (chamfer or radius) applied at one end of a cylinder or taper.</summary>
public sealed class EdgeBreak : ModelBase
{
    private EdgeKind _kind; private double _size;
    public EdgeKind Kind { get => _kind; set => Set(ref _kind, value); }
    /// <summary>Chamfer leg length or fillet radius, mm.</summary>
    public double Size { get => _size; set => Set(ref _size, value); }
    public bool IsSet => Kind != EdgeKind.None && Size > 0;
    public override string Summary => Kind switch
    {
        EdgeKind.Chamfer => $"C{Num.F(Size)}",
        EdgeKind.Radius => $"R{Num.F(Size)}",
        _ => ""
    };
    public static EdgeBreak None() => new();
    public static EdgeBreak Ch(double s) => new() { Kind = EdgeKind.Chamfer, Size = s };
    public static EdgeBreak Rad(double s) => new() { Kind = EdgeKind.Radius, Size = s };
}

/// <summary>
/// A profile feature, listed from the face (right) toward the chuck (left).
/// Diameters are true diameters in mm; lengths are along Z in mm.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CylinderFeature), "cylinder")]
[JsonDerivedType(typeof(TaperFeature), "taper")]
[JsonDerivedType(typeof(ArcFeature), "arc")]
public abstract class Feature : ModelBase
{
    private double _length = 10;
    public double Length { get => _length; set => Set(ref _length, value); }
    [JsonIgnore] public abstract string TypeName { get; }

    private readonly HashSet<EdgeBreak> _hooked = new(ReferenceEqualityComparer.Instance);
    /// <summary>Re-raise Summary when a nested edge break changes so lists stay current.</summary>
    protected EdgeBreak Hook(EdgeBreak e) { Watch(e); OnChanged(nameof(Summary)); return e; }
    protected EdgeBreak Watch(EdgeBreak e)
    {
        if (_hooked.Add(e)) e.PropertyChanged += (_, _) => OnChanged(nameof(Summary));
        return e;
    }
}

public sealed class CylinderFeature : Feature
{
    private double _dia = 20;
    public double Diameter { get => _dia; set => Set(ref _dia, value); }
    private EdgeBreak _startEdge = new(), _endEdge = new();
    public EdgeBreak StartEdge { get => Watch(_startEdge); set => _startEdge = Hook(value); }
    public EdgeBreak EndEdge { get => Watch(_endEdge); set => _endEdge = Hook(value); }
    public override string TypeName => "Diameter";
    public override string Summary => $"Ø{Num.F(Diameter)} × {Num.F(Length)}" + Edges(StartEdge, EndEdge);
    internal static string Edges(EdgeBreak s, EdgeBreak e) =>
        (s.IsSet ? $"  right {s.Summary}" : "") + (e.IsSet ? $"  left {e.Summary}" : "");
}

public sealed class TaperFeature : Feature
{
    private double _d1 = 20, _d2 = 15;
    public double StartDiameter { get => _d1; set => Set(ref _d1, value); }
    public double EndDiameter { get => _d2; set => Set(ref _d2, value); }
    private EdgeBreak _startEdge = new(), _endEdge = new();
    public EdgeBreak StartEdge { get => Watch(_startEdge); set => _startEdge = Hook(value); }
    public EdgeBreak EndEdge { get => Watch(_endEdge); set => _endEdge = Hook(value); }
    public override string TypeName => "Taper";
    /// <summary>Included half-angle in degrees (informational).</summary>
    [JsonIgnore] public double HalfAngle => Length <= 0 ? 0 : Math.Atan(Math.Abs(StartDiameter - EndDiameter) / 2 / Length) * 180 / Math.PI;
    public override string Summary => $"Taper Ø{Num.F(StartDiameter)} → Ø{Num.F(EndDiameter)} × {Num.F(Length)} ({Num.F(HalfAngle, 2)}°)" + CylinderFeature.Edges(StartEdge, EndEdge);
}

/// <summary>A circular arc from the current point to (EndDiameter, Length further left).</summary>
public sealed class ArcFeature : Feature
{
    private double _dEnd = 20, _radius = 10; private bool _convex = true;
    public double EndDiameter { get => _dEnd; set => Set(ref _dEnd, value); }
    public double Radius { get => _radius; set => Set(ref _radius, value); }
    /// <summary>True = bulges outward (ball), false = hollow (cove).</summary>
    public bool Convex { get => _convex; set => Set(ref _convex, value); }
    public override string TypeName => "Arc";
    public override string Summary => $"{(Convex ? "Convex" : "Concave")} R{Num.F(Radius)} → Ø{Num.F(EndDiameter)} × {Num.F(Length)}";
}

/// <summary>An external groove cut with a grooving tool. Position is the distance from Z0 to the groove's right wall.</summary>
public sealed class Groove : ModelBase
{
    private double _pos = 10, _width = 3, _dia = 16;
    public double Position { get => _pos; set => Set(ref _pos, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Diameter { get => _dia; set => Set(ref _dia, value); }
    public override string Summary => $"Groove Ø{Num.F(Diameter)} × {Num.F(Width)} wide at {Num.F(Position)}";
}

/// <summary>A single-point thread on an existing diameter (external) or bore (internal). Right-hand only.</summary>
public sealed class ThreadFeature : ModelBase
{
    private string _spec = "M10x1.5"; private double _pos, _length = 15; private bool _internal;
    private double _customMajor, _customPitch;
    /// <summary>Name from the thread table (e.g. "M10x1.5", "1/2-13 UNC") or "Custom".</summary>
    public string Spec { get => _spec; set => Set(ref _spec, value); }
    /// <summary>Distance from Z0 to the thread's right end.</summary>
    public double Position { get => _pos; set => Set(ref _pos, value); }
    public double Length { get => _length; set => Set(ref _length, value); }
    public bool Internal { get => _internal; set => Set(ref _internal, value); }
    public double CustomMajorDia { get => _customMajor; set => Set(ref _customMajor, value); }
    public double CustomPitch { get => _customPitch; set => Set(ref _customPitch, value); }
    public override string Summary => $"{(Internal ? "Internal" : "External")} thread {Spec} × {Num.F(Length)} at {Num.F(Position)}";
}
