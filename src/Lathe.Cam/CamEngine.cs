using Lathe.Cam.Ops;
using Lathe.Core;
using Lathe.Core.Materials;
using Lathe.Core.Model;
using Lathe.Core.Project;
using Lathe.Core.Tools;

namespace Lathe.Cam;

public sealed class CamOptions
{
    /// <summary>Spindle encoder available (G95 + G33). Null = use the machine profile.</summary>
    public bool? SpindleSync { get; init; }
    public bool Simulate { get; init; } = true;
}

public sealed class CamResult
{
    public PartGeometry Geometry { get; init; } = null!;
    public CamContext? Context { get; init; }
    public List<OpPath> Ops { get; } = new();
    public List<Issue> Issues { get; } = new();
    public SimResult? Sim { get; set; }
    public bool SpindleSync { get; init; }
    public IEnumerable<Issue> AllIssues => Issues.Concat(Ops.SelectMany(o => o.Issues.Select(i => i with { Where = i.Where ?? o.Name }))).Concat(Sim?.Issues ?? new());
    public bool Ok => Ops.Count > 0 && !AllIssues.HasErrors();
    public double TotalSeconds => Ops.Sum(o => o.Seconds);
}

/// <summary>Turns a project into validated, simulated toolpaths.</summary>
public static class CamEngine
{
    public static CamResult Generate(LatheProject project, MachineProfile machine, CamOptions? options = null)
    {
        options ??= new CamOptions();
        bool sync = options.SpindleSync ?? machine.SpindleSync;
        var geo = PartBuilder.Build(project.Stock, project.Part);
        var result = new CamResult { Geometry = geo, SpindleSync = sync };
        result.Issues.AddRange(geo.Issues);
        foreach (var t in project.Tools) result.Issues.AddRange(t.Validate());
        foreach (var dup in project.Tools.GroupBy(t => t.Number).Where(g => g.Count() > 1))
            result.Issues.Add(Issue.Err($"Tool number T{dup.Key} is used by more than one tool."));
        if (!geo.Ok) return result;

        var ops = project.Operations.Select((o, i) => (op: o, index: i)).Where(x => x.op.Enabled).ToList();
        if (ops.Count == 0) { result.Issues.Add(Issue.Err("No operations enabled.")); return result; }

        var ctx = new CamContext(project, machine, geo);
        var mat = MaterialTable.Find(project.Material);

        // Pre-pass: holes and part-off width are part of the finished shape.
        bool facedSoFar = false;
        foreach (var (op, _) in ops)
        {
            if (op is FaceOp) facedSoFar = true;
            var tool = project.Tool(op.ToolNumber);
            if (op is DrillOp d && tool is { Type: ToolType.Drill or ToolType.CenterDrill })
                ctx.AddDrillHole(tool.Diameter, d.Depth, ToolShapes.DrillPointLength(tool), facedSoFar ? 0 : geo.StockFaceZ);
            if (op is PartOffOp && tool is not null) ctx.PartOffWidth = tool.Width;
        }
        ctx.Complete();
        CheckOrder(project, geo, ops.Select(x => x.op).ToList(), result.Issues);

        var st = new CamState { HoleR = geo.StockBoreR };
        foreach (var (op, index) in ops)
        {
            var tool = project.Tool(op.ToolNumber);
            if (tool is null) { result.Issues.Add(Issue.Err($"Tool T{op.ToolNumber} is not in the tool list.", op.Title)); continue; }
            string? bad = Compatible(op, tool);
            if (bad is not null) { result.Issues.Add(Issue.Err(bad, op.Title)); continue; }
            var cut = CutFor(op, tool, mat, geo, machine, out var rpmNote);
            var path = new OpPath { OpIndex = index, Name = $"{index + 1}. {op.Title}", Tool = tool, Rpm = cut.Rpm, Coolant = op.Coolant, Start = ctx.ToolChange };
            if (rpmNote is not null) path.Issues.Add(Issue.Warn(rpmNote));
            try
            {
                switch (op)
                {
                    case FaceOp f: Facing.Generate(ctx, f, tool, path, st, cut); break;
                    case TurnOp t: Turning.Generate(ctx, t, tool, path, st, cut); break;
                    case DrillOp d: Drilling.Generate(ctx, d, tool, path, st, cut); break;
                    case GrooveOp g: Grooving.Generate(ctx, g, tool, path, st, cut); break;
                    case ThreadOp th: Threading.Generate(ctx, th, tool, path, st, sync); break;
                    case PartOffOp p: PartingOff.Generate(ctx, p, tool, path, st, cut); break;
                }
            }
            catch (Exception e)
            {
                path.Issues.Add(Issue.Err($"Toolpath generation failed: {e.Message}"));
            }
            path.Seconds = EstimateSeconds(path, machine);
            result.Ops.Add(path);
        }

        if (options.Simulate && result.Ops.Count > 0 && !result.Ops.Any(o => o.Issues.HasErrors()))
            result.Sim = StockSim.Run(ctx, result.Ops);
        return new CamResult { Geometry = geo, Context = ctx, SpindleSync = sync, Sim = result.Sim }.Fill(result);
    }

    private static CamResult Fill(this CamResult r, CamResult src)
    {
        r.Ops.AddRange(src.Ops); r.Issues.AddRange(src.Issues); return r;
    }

    private static string? Compatible(OpDef op, ToolDef t) => op switch
    {
        FaceOp when t.Type != ToolType.Turning => $"Facing needs a turning tool; T{t.Number} is {t.Type}.",
        TurnOp { Side: TurnSide.Outside } when t.Type != ToolType.Turning => $"OD turning needs a turning tool; T{t.Number} is {t.Type}.",
        TurnOp { Side: TurnSide.Inside } when t.Type != ToolType.Boring => $"ID turning needs a boring bar; T{t.Number} is {t.Type}.",
        DrillOp when t.Type is not (ToolType.Drill or ToolType.CenterDrill) => $"Drilling needs a drill; T{t.Number} is {t.Type}.",
        GrooveOp when t.Type is not (ToolType.Grooving or ToolType.PartOff) => $"Grooving needs a grooving tool; T{t.Number} is {t.Type}.",
        ThreadOp { Internal: false } when t.Type != ToolType.ThreadExternal => $"External threading needs an OD threading tool; T{t.Number} is {t.Type}.",
        ThreadOp { Internal: true } when t.Type != ToolType.ThreadInternal => $"Internal threading needs an ID threading tool; T{t.Number} is {t.Type}.",
        PartOffOp when t.Type is not (ToolType.PartOff or ToolType.Grooving) => $"Part-off needs a parting blade; T{t.Number} is {t.Type}.",
        _ => null,
    };

    private static void CheckOrder(LatheProject p, PartGeometry g, List<OpDef> ops, List<Issue> issues)
    {
        int partIdx = ops.FindIndex(o => o is PartOffOp);
        if (partIdx >= 0 && partIdx != ops.Count - 1) issues.Add(Issue.Err("Part-off must be the last operation."));
        if (partIdx >= 0 && !g.PartOff) issues.Add(Issue.Err("There is a part-off operation but 'Part off' is unticked on the part."));
        if (partIdx < 0 && g.PartOff) issues.Add(Issue.Warn("'Part off' is ticked but there is no part-off operation; the part stays on the bar."));
        if (g.Grooves.Count > 0 && !ops.Any(o => o is GrooveOp)) issues.Add(Issue.Warn("The part has grooves but no grooving operation."));
        if (g.Threads.Any(t => !t.T.Internal) && !ops.Any(o => o is ThreadOp { Internal: false })) issues.Add(Issue.Warn("The part has an external thread but no threading operation."));
        if (g.Threads.Any(t => t.T.Internal) && !ops.Any(o => o is ThreadOp { Internal: true })) issues.Add(Issue.Warn("The part has an internal thread but no internal threading operation."));
        if (!g.Id.IsEmpty && !ops.Any(o => o is TurnOp { Side: TurnSide.Inside })) issues.Add(Issue.Warn("The part has a bore but no ID turning operation."));
        if (!ops.Any(o => o is TurnOp { Side: TurnSide.Outside, Stage: TurnStage.Finish })) issues.Add(Issue.Warn("No OD finish pass: surface will be left at the roughing allowance."));
        foreach (var side in new[] { TurnSide.Outside, TurnSide.Inside })
        {
            int fin = ops.FindIndex(o => o is TurnOp t && t.Side == side && t.Stage == TurnStage.Finish);
            int rough = ops.FindIndex(o => o is TurnOp t && t.Side == side && t.Stage == TurnStage.Rough);
            if (fin >= 0 && (rough < 0 || rough > fin))
            {
                double depth = side == TurnSide.Outside ? g.StockR - g.Od.ToPolyline(0.05).Where(q => q.Z < -0.01).DefaultIfEmpty().Min(q => q.R) : 1;
                if (depth > 1.0) issues.Add(Issue.Warn($"{(side == TurnSide.Outside ? "OD" : "ID")} finish without roughing first: it will take up to a {Num.F(depth)} mm cut in one pass."));
            }
        }
        int groove = ops.FindIndex(o => o is GrooveOp), thread = ops.FindIndex(o => o is ThreadOp { Internal: false });
        int odFinish = ops.FindIndex(o => o is TurnOp { Side: TurnSide.Outside, Stage: TurnStage.Finish });
        if (groove >= 0 && odFinish > groove) issues.Add(Issue.Warn("Grooving before the OD finish pass: the finish pass will cut air over the grooves. Usually groove after finishing."));
        if (thread >= 0 && odFinish > thread) issues.Add(Issue.Err("Threading must come after the OD finish pass."));
        int drill = ops.FindIndex(o => o is DrillOp), idRough = ops.FindIndex(o => o is TurnOp { Side: TurnSide.Inside });
        if (idRough >= 0 && g.StockBoreR <= 0 && (drill < 0 || drill > idRough)) issues.Add(Issue.Err("Boring needs a drilled hole first: put a Drill operation before ID turning."));
    }

    private static CutParams CutFor(OpDef op, ToolDef tool, MaterialSpec mat, PartGeometry g, MachineProfile m, out string? note)
    {
        note = null;
        int max = m.EffectiveMaxRpm, min = Math.Max(1, m.MinRpm);
        CutData d; double dia; double feed, finFeed, doc;
        var rough = mat.Get(tool.Material, CutKind.Rough); var fin = mat.Get(tool.Material, CutKind.Finish);
        double maxProfileDia = 2 * g.Od.ToPolyline(0.05).Max(p => p.R);
        switch (op)
        {
            case FaceOp: d = rough; dia = 2 * g.StockR; feed = rough.FeedPerRev; finFeed = fin.FeedPerRev; doc = rough.DepthOfCut; break;
            case TurnOp { Stage: TurnStage.Rough } t:
                d = rough; dia = t.Side == TurnSide.Outside ? 2 * g.StockR : 2 * g.Id.ToPolyline(0.05).Max(p => p.R);
                feed = rough.FeedPerRev * (t.Side == TurnSide.Inside ? 0.7 : 1); finFeed = feed; doc = rough.DepthOfCut * (t.Side == TurnSide.Inside ? 0.5 : 1); break;
            case TurnOp t:
                d = fin; dia = t.Side == TurnSide.Outside ? maxProfileDia : 2 * g.Id.ToPolyline(0.05).Max(p => p.R);
                feed = finFeed = fin.FeedPerRev; doc = 0; break;
            case DrillOp:
                d = mat.Get(tool.Material, CutKind.Drill); dia = tool.Diameter;
                feed = finFeed = FeedsSpeeds.DrillFeed(tool.Diameter) * (tool.Type == ToolType.CenterDrill ? 0.5 : 1); doc = 0; break;
            case GrooveOp:
                d = mat.Get(tool.Material, CutKind.Groove); dia = maxProfileDia; feed = d.FeedPerRev; finFeed = d.FeedPerRev * 0.7; doc = 0; break;
            case ThreadOp th:
                d = mat.Get(tool.Material, CutKind.Thread); dia = g.Threads.Where(t => t.T.Internal == th.Internal).Select(t => t.Spec.MajorDia).DefaultIfEmpty(10).Max();
                feed = finFeed = 0; doc = 0; max = Math.Min(max, 500); break;
            case PartOffOp:
                d = mat.Get(tool.Material, CutKind.PartOff); dia = 2 * g.StockR; feed = finFeed = d.FeedPerRev; doc = 0; max = Math.Min(max, 1200); break;
            default: d = rough; dia = 2 * g.StockR; feed = finFeed = rough.FeedPerRev; doc = 1; break;
        }
        int rpm = FeedsSpeeds.Rpm(d.Vc, dia, max, min);
        if (op.Rpm > 0)
        {
            rpm = op.Rpm;
            if (rpm > m.EffectiveMaxRpm) { note = $"Requested {op.Rpm} rpm exceeds the machine/chuck limit; using {m.EffectiveMaxRpm}."; rpm = m.EffectiveMaxRpm; }
            if (rpm < min) rpm = min;
        }
        if (op.Feed > 0) { feed = op.Feed; finFeed = op is TurnOp { Stage: TurnStage.Rough } ? op.Feed : op.Feed; }
        return new CutParams(feed, finFeed, doc, rpm);
    }

    public static double EstimateSeconds(OpPath p, MachineProfile m)
    {
        double rapid = Math.Max(100, Math.Min(m.MaxFeedX, m.MaxFeedZ));
        double t = m.SpindleSpinUpSeconds;
        var pos = p.Start;
        foreach (var mv in p.Moves)
        {
            double len = pos.DistanceTo(mv.To);
            double rate = mv.Kind switch
            {
                MoveKind.Rapid => rapid,
                MoveKind.Feed => Math.Max(1, mv.Feed * p.Rpm),
                _ => Math.Max(1, mv.Pitch * p.Rpm),
            };
            t += len / rate * 60;
            pos = mv.To;
        }
        return t;
    }
}
