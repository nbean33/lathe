using System.Text.Json.Serialization;

namespace Lathe.Core.Model;

public enum TurnSide { Outside, Inside }
public enum TurnStage { Rough, Finish }

/// <summary>
/// One machining step. Feeds are per revolution (mm/rev); 0 means "use the material table".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(FaceOp), "face")]
[JsonDerivedType(typeof(TurnOp), "turn")]
[JsonDerivedType(typeof(DrillOp), "drill")]
[JsonDerivedType(typeof(GrooveOp), "groove")]
[JsonDerivedType(typeof(ThreadOp), "thread")]
[JsonDerivedType(typeof(PartOffOp), "partoff")]
public abstract class OpDef : ModelBase
{
    private bool _enabled = true, _coolant; private int _tool = 1, _rpm; private double _feed;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int ToolNumber { get => _tool; set => Set(ref _tool, value); }
    /// <summary>Spindle speed override (0 = auto from material).</summary>
    public int Rpm { get => _rpm; set => Set(ref _rpm, value); }
    /// <summary>Feed override, mm/rev (0 = auto).</summary>
    public double Feed { get => _feed; set => Set(ref _feed, value); }
    public bool Coolant { get => _coolant; set => Set(ref _coolant, value); }
    [JsonIgnore] public abstract string Title { get; }
    public override string Summary => $"{Title}  (T{ToolNumber})";
}

public sealed class FaceOp : OpDef
{
    private double _doc = 0.5, _finish = 0.1;
    /// <summary>Z depth per facing pass.</summary>
    public double DepthOfCut { get => _doc; set => Set(ref _doc, value); }
    /// <summary>Last pass thickness (0 = no separate finish pass).</summary>
    public double FinishPass { get => _finish; set => Set(ref _finish, value); }
    public override string Title => "Face";
}

public sealed class TurnOp : OpDef
{
    private TurnSide _side; private TurnStage _stage; private double _doc = 1.0, _allow = 0.2; private bool _pockets = true;
    public TurnSide Side { get => _side; set => Set(ref _side, value); }
    public TurnStage Stage { get => _stage; set => Set(ref _stage, value); }
    /// <summary>Radial depth per roughing pass.</summary>
    public double DepthOfCut { get => _doc; set => Set(ref _doc, value); }
    /// <summary>Material left for the finish pass (radial and on faces).</summary>
    public double Allowance { get => _allow; set => Set(ref _allow, value); }
    /// <summary>Rough into hollows the tool can ramp into.</summary>
    public bool AllowPockets { get => _pockets; set => Set(ref _pockets, value); }
    public override string Title => $"{(Side == TurnSide.Outside ? "OD" : "ID")} {(Stage == TurnStage.Rough ? "rough" : "finish")}";
}

public sealed class DrillOp : OpDef
{
    private double _depth = 20, _peck = 3, _chip = 0.5; private int _fullEvery = 3;
    /// <summary>Depth of the full drill diameter from Z0 (the point goes further).</summary>
    public double Depth { get => _depth; set => Set(ref _depth, value); }
    /// <summary>Peck depth (0 = no pecking).</summary>
    public double PeckDepth { get => _peck; set => Set(ref _peck, value); }
    public double ChipBreakRetract { get => _chip; set => Set(ref _chip, value); }
    /// <summary>Pull fully out every N pecks to clear chips.</summary>
    public int FullRetractEvery { get => _fullEvery; set => Set(ref _fullEvery, value); }
    public override string Title => "Drill";
}

public sealed class GrooveOp : OpDef
{
    private double _peck = 1.0, _step = 0.8, _finish = 0.05;
    public double PeckDepth { get => _peck; set => Set(ref _peck, value); }
    /// <summary>Step-over as a fraction of blade width.</summary>
    public double StepOver { get => _step; set => Set(ref _step, value); }
    public double FinishAllowance { get => _finish; set => Set(ref _finish, value); }
    public override string Title => "Groove (all grooves)";
}

public sealed class ThreadOp : OpDef
{
    private double _first = 0.3, _min = 0.03, _infeed = 29.5; private int _spring = 1; private bool _internal;
    public bool Internal { get => _internal; set => Set(ref _internal, value); }
    /// <summary>Radial depth of the first pass.</summary>
    public double FirstDepth { get => _first; set => Set(ref _first, value); }
    public double MinDepth { get => _min; set => Set(ref _min, value); }
    public int SpringPasses { get => _spring; set => Set(ref _spring, value); }
    /// <summary>Compound (flank) infeed angle; 0 = straight in.</summary>
    public double InfeedAngle { get => _infeed; set => Set(ref _infeed, value); }
    public override string Title => Internal ? "Thread (internal)" : "Thread (external)";
}

public sealed class PartOffOp : OpDef
{
    private double _peck = 1.5, _slowPct = 30, _slowFactor = 0.5, _endDia = -0.4;
    public double PeckDepth { get => _peck; set => Set(ref _peck, value); }
    /// <summary>Below this % of the start diameter, feed is reduced.</summary>
    public double SlowBelowPercent { get => _slowPct; set => Set(ref _slowPct, value); }
    public double SlowFeedFactor { get => _slowFactor; set => Set(ref _slowFactor, value); }
    /// <summary>X (diameter) where the cut stops. Slightly negative cuts through; positive leaves a pip.</summary>
    public double EndDiameter { get => _endDia; set => Set(ref _endDia, value); }
    public override string Title => "Part off";
}
