using Clipper2Lib;
using Lathe.Core;
using Lathe.Core.Geometry;
using Lathe.Core.Tools;

namespace Lathe.Cam;

public sealed class SimResult
{
    public PathsD FinalStock { get; init; } = new();
    public PathsD Gouges { get; init; } = new();
    public PathsD Uncut { get; init; } = new();
    public List<Issue> Issues { get; } = new();
    /// <summary>Snapshot of the stock after each operation (for scrubbing in the UI).</summary>
    public List<PathsD> StockAfterOp { get; } = new();
}

/// <summary>
/// 2D material-removal simulation in the half section. Sweeps the real tool outline along every move,
/// removes material (mirrored about the axis, since the part spins), and reports collisions:
/// rapids through material, holders rubbing, chuck hits, gouges into the finished part and leftover material.
/// </summary>
public static class StockSim
{
    private const int Batch = 25;

    public static SimResult Run(CamContext ctx, IReadOnlyList<OpPath> ops)
    {
        var stock = ctx.StockUpper;
        var res = new SimResult();
        foreach (var op in ops)
        {
            var shape = ToolShapes.For(op.Tool);
            var pos = op.Start;
            var cutBatch = new PathsD(); var holderBatch = new PathsD();
            int batchStart = 0, n = 0;
            bool rapidErr = false, holderErr = false, chuckErr = false, negErr = false;

            void Flush(int idx)
            {
                if (cutBatch.Count == 0 && holderBatch.Count == 0) return;
                if (cutBatch.Count > 0) stock = Geo.Diff(stock, Geo.Union(cutBatch));
                if (holderBatch.Count > 0 && (op.Tool.IsAxial || op.Tool.IsInside)) holderBatch = Geo.Full(Geo.Union(holderBatch));
                var all = Geo.Union(cutBatch, holderBatch);
                if (!chuckErr && Geo.Area(Geo.Inter(all, ctx.Chuck)) > 1e-3)
                { res.Issues.Add(Issue.Err($"Collision with the chuck (moves {batchStart + 1}-{idx + 1}).", op.Name)); chuckErr = true; }
                if (!holderErr && holderBatch.Count > 0)
                {
                    var hit = Geo.Inter(Geo.Union(holderBatch), stock);
                    if (Geo.Area(hit) > 1e-2)
                    {
                        var b = Geo.Bounds(hit);
                        res.Issues.Add(Issue.Err($"Tool holder runs into material near Z{Num.F(b.zMax)} Ø{Num.F(2 * b.rMax)} (moves {batchStart + 1}-{idx + 1}).", op.Name));
                        holderErr = true;
                    }
                }
                cutBatch.Clear(); holderBatch.Clear(); batchStart = idx + 1; n = 0;
            }

            for (int i = 0; i < op.Moves.Count; i++)
            {
                var m = op.Moves[i];
                if (!negErr && m.To.R < -1.0)
                { res.Issues.Add(Issue.Err($"Move goes to X{Num.F(2 * m.To.R)} (below center).", op.Name)); negErr = true; }
                if (m.Kind == MoveKind.Rapid)
                {
                    Flush(i - 1);
                    var sw = Geo.Full(Geo.Sweep(shape.All, pos, m.To));
                    var hit = Geo.Inter(sw, stock);
                    if (!rapidErr && Geo.Area(hit) > 1e-2)
                    {
                        var b = Geo.Bounds(hit);
                        res.Issues.Add(Issue.Err($"Rapid move {i + 1} would crash into material near Z{Num.F(b.zMax)} Ø{Num.F(2 * b.rMax)}.", op.Name));
                        rapidErr = true;
                    }
                    if (!chuckErr && Geo.Area(Geo.Inter(sw, ctx.Chuck)) > 1e-3)
                    { res.Issues.Add(Issue.Err($"Rapid move {i + 1} hits the chuck.", op.Name)); chuckErr = true; }
                    if (hit.Count > 0) stock = Geo.Diff(stock, sw);
                }
                else
                {
                    cutBatch.AddRange(Geo.Full(Geo.Sweep(shape.Cutting, pos, m.To)));
                    holderBatch.AddRange(Geo.Sweep(shape.Holder, pos, m.To));
                    if (++n >= Batch) Flush(i);
                }
                pos = m.To;
            }
            Flush(op.Moves.Count - 1);
            res.StockAfterOp.Add(stock);
        }

        // Compare with the finished part.
        var removed = Geo.Diff(ctx.StockUpper, stock);
        var keep = Geo.Union(ctx.PartUpper, ctx.BarRemainderUpper);
        var protect = Geo.Diff(Geo.Inflate(keep, -0.01), ctx.ThreadZonesUpper);
        var gouge = Geo.Inter(protect, removed);
        if (Geo.Area(gouge) > 1e-3)
        {
            var b = Geo.Bounds(gouge);
            res.Issues.Add(Issue.Err($"Gouge: the program cuts into the finished part near Z{Num.F(b.zMax)}…Z{Num.F(b.zMin)}, Ø{Num.F(2 * b.rMax)}."));
        }
        var uncut = Geo.Diff(stock, Geo.Inflate(keep, 0.03));
        uncut = new PathsD(uncut.Where(p => Math.Abs(Clipper.Area(p)) > 0.02));
        if (Geo.Area(uncut) > 0.05)
        {
            var b = Geo.Bounds(uncut);
            res.Issues.Add(Issue.Warn($"Material left on the part near Z{Num.F(b.zMax)}…Z{Num.F(b.zMin)}, Ø{Num.F(2 * b.rMin)}–Ø{Num.F(2 * b.rMax)} ({Num.F(Geo.Area(uncut), 2)} mm² in section). A different tool or operation may be needed."));
        }
        return new SimResult { FinalStock = stock, Gouges = gouge, Uncut = uncut }.With(res);
    }

    private static SimResult With(this SimResult target, SimResult src)
    {
        target.Issues.AddRange(src.Issues);
        target.StockAfterOp.AddRange(src.StockAfterOp);
        return target;
    }
}
