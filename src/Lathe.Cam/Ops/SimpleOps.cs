using Lathe.Core;
using Lathe.Core.Geometry;
using Lathe.Core.Model;
using Lathe.Core.Threads;
using Lathe.Core.Tools;

namespace Lathe.Cam.Ops;

/// <summary>State carried from one operation to the next.</summary>
internal sealed class CamState
{
    public bool Faced;
    /// <summary>Largest hole radius made so far (tube bore or drills).</summary>
    public double HoleR;
    public double DrillDepth;
}

/// <summary>Resolved cutting data for an operation (feeds in mm/rev).</summary>
internal sealed record CutParams(double Feed, double FinishFeed, double Doc, int Rpm);

internal static class Facing
{
    public static void Generate(CamContext ctx, FaceOp op, ToolDef tool, OpPath path, CamState st, CutParams cut)
    {
        var g = ctx.G;
        double clr = Math.Max(0.3, ctx.Machine.Clearance);
        double total = g.StockFaceZ;
        if (total <= 0.001) { path.Issues.Add(Issue.Note("Face allowance is zero: facing makes one clean-up pass at Z0.")); }
        double doc = Math.Max(0.05, op.DepthOfCut);
        double fin = Math.Clamp(op.FinishPass, 0, doc);
        double rStart = g.StockR + clr;
        double rEnd = g.StockBoreR > 0 ? g.StockBoreR - 0.5 : -(tool.NoseRadius + 0.1);
        if (st.HoleR > 0) rEnd = Math.Min(rEnd, st.HoleR - 0.5);
        rEnd = Math.Max(rEnd, -(tool.NoseRadius + 0.3));

        var levels = new List<(double z, bool finish)>();
        double z = total;
        while (z - doc > fin + 1e-6) { z -= doc; levels.Add((z, false)); }
        if (fin > 1e-6 && total > fin + 1e-6) levels.Add((fin, false));
        levels.Add((0, true));

        var w = new PathWriter(path);
        w.Rapid(total + clr, rStart);
        foreach (var (lz, finish) in levels)
        {
            w.Rapid(lz + clr, rStart);
            w.Rapid(lz, rStart);
            w.Feed(lz, rEnd, finish ? cut.FinishFeed : cut.Feed);
            w.Rapid(lz + Math.Min(0.5, clr), rEnd);
            w.Rapid(lz + Math.Min(0.5, clr), rStart);
        }
        w.Home();
        st.Faced = true;
    }
}

internal static class Drilling
{
    public static void Generate(CamContext ctx, DrillOp op, ToolDef tool, OpPath path, CamState st, CutParams cut)
    {
        var g = ctx.G;
        double hc = ToolShapes.DrillPointLength(tool);
        double zTop = st.Faced ? 0 : g.StockFaceZ;
        double zClear = zTop + Math.Max(1, ctx.Machine.Clearance + 1);
        double tip = -(op.Depth + hc);
        if (op.Depth <= 0) { path.Issues.Add(Issue.Err("Drill depth must be greater than zero.")); return; }
        if (op.Depth + hc > tool.MaxDepth + 1e-6) path.Issues.Add(Issue.Err($"Hole is deeper ({Num.F(op.Depth + hc)}) than {tool.Summary} can reach ({Num.F(tool.MaxDepth)})."));
        if (tip < g.JawZ + 2) path.Issues.Add(Issue.Err($"Drill would reach the chuck jaws (tip at Z{Num.F(tip)}, jaws at Z{Num.F(g.JawZ)})."));
        if (tool.Diameter / 2 >= g.StockR - 0.5) path.Issues.Add(Issue.Err("Drill is as big as the stock."));

        var w = new PathWriter(path);
        w.Rapid(zClear, path.Start.R);
        w.Rapid(zClear, 0);
        double q = op.PeckDepth;
        if (q <= 0)
        {
            w.Feed(tip, 0, cut.Feed);
        }
        else
        {
            double done = zTop; int n = 0;
            w.Rapid(zTop + 0.5, 0);
            while (done > tip + 1e-6)
            {
                if (n > 0)
                {
                    if (op.FullRetractEvery > 0 && n % op.FullRetractEvery == 0) { w.Rapid(zClear, 0); w.Rapid(done + 0.5, 0); }
                    else w.Rapid(done + Math.Max(0.1, op.ChipBreakRetract), 0);
                }
                double next = Math.Max(tip, done - q);
                w.Feed(next, 0, cut.Feed);
                done = next; n++;
                if (n > 2000) { path.Issues.Add(Issue.Err("Too many pecks; increase peck depth.")); break; }
            }
        }
        w.Rapid(zClear, 0);
        w.Home();
        if (tool.Type == ToolType.Drill)
        {
            st.HoleR = Math.Max(st.HoleR, tool.Diameter / 2);
            st.DrillDepth = Math.Max(st.DrillDepth, op.Depth);
        }
    }
}

internal static class Grooving
{
    public static void Generate(CamContext ctx, GrooveOp op, ToolDef tool, OpPath path, CamState st, CutParams cut)
    {
        var g = ctx.G;
        double clr = Math.Max(0.3, ctx.Machine.Clearance);
        double tw = tool.Width;
        double ySafe = g.StockR + clr;
        if (g.Grooves.Count == 0) { path.Issues.Add(Issue.Warn("No grooves on the part; nothing to do.")); return; }
        var w = new PathWriter(path);
        foreach (var gr in g.Grooves.OrderByDescending(x => x.ZRight))
        {
            double width = gr.ZRight - gr.ZLeft;
            string where = gr.G.Summary;
            if (tw > width + 1e-6) { path.Issues.Add(Issue.Err($"{tool.Summary} ({Num.F(tw)} wide) is wider than the groove ({Num.F(width)}).", where)); continue; }
            double surf = 0;
            for (double z = gr.ZRight; z >= gr.ZLeft - 1e-9; z -= Math.Max(0.05, width / 10)) surf = Math.Max(surf, g.Od.RadiusAt(z) ?? g.StockR);
            if (surf - gr.R > tool.MaxDepth) { path.Issues.Add(Issue.Err($"Groove is {Num.F(surf - gr.R)} deep but {tool.Summary} reaches {Num.F(tool.MaxDepth)}.", where)); continue; }
            double fa = width - 2 * op.FinishAllowance >= tw ? op.FinishAllowance : 0;
            double zMax = gr.ZRight - fa, zMin = gr.ZLeft + tw + fa;
            double yC = surf + clr;
            int n = Math.Max(1, (int)Math.Ceiling((zMax - zMin) / Math.Max(0.1, op.StepOver * tw) - 1e-9) + 1);
            if (zMax - zMin < 1e-6) n = 1;
            double peck = op.PeckDepth > 0 ? op.PeckDepth : double.MaxValue;
            double bottom = gr.R + fa;

            w.Rapid(w.Cur.Z, Math.Max(w.Cur.R, ySafe));
            w.Rapid(zMax, Math.Max(w.Cur.R, ySafe));
            for (int i = 0; i < n; i++)
            {
                double z = n == 1 ? zMax : zMax - (zMax - zMin) * i / (n - 1);
                w.Rapid(z, yC);
                double depth = surf;
                while (depth > bottom + 1e-6)
                {
                    double next = Math.Max(bottom, depth - peck);
                    if (depth < surf) w.Rapid(z, depth + 0.3);
                    w.Feed(z, next, cut.Feed);
                    depth = next;
                }
                w.Rapid(z, yC);
            }
            // Finish walls and floor.
            w.Rapid(gr.ZRight, yC);
            w.Feed(gr.ZRight, gr.R, cut.FinishFeed);
            w.Feed(gr.ZLeft + tw, gr.R, cut.FinishFeed);
            w.Feed(gr.ZLeft + tw, yC, cut.FinishFeed);
            w.Rapid(gr.ZLeft + tw, ySafe);
        }
        w.Home();
    }
}

internal static class Threading
{
    public static void Generate(CamContext ctx, ThreadOp op, ToolDef tool, OpPath path, CamState st, bool spindleSync)
    {
        var g = ctx.G;
        if (!spindleSync) { path.Issues.Add(Issue.Err("Threading needs spindle sync (an index/encoder on the spindle). Enable it in the machine settings if your controller has it.")); return; }
        var threads = g.Threads.Where(t => t.T.Internal == op.Internal).ToList();
        if (threads.Count == 0) { path.Issues.Add(Issue.Warn($"No {(op.Internal ? "internal" : "external")} threads on the part.")); return; }
        double clr = Math.Max(0.3, ctx.Machine.Clearance);
        var w = new PathWriter(path);
        foreach (var t in threads)
        {
            var sp = t.Spec;
            double pitch = sp.Pitch;
            double v = pitch * path.Rpm / 60.0; // mm/s
            double maxFeed = ctx.Machine.MaxFeedZ;
            if (pitch * path.Rpm > maxFeed)
            {
                int rpm = (int)Math.Floor(maxFeed / pitch * 0.9);
                path.Issues.Add(Issue.Warn($"Spindle reduced to {rpm} rpm so {sp.Name} stays under the Z feed limit."));
                path.Rpm = rpm; v = pitch * rpm / 60.0;
            }
            double accelDist = v * v / (2 * Math.Max(10, ctx.Machine.AccelZ));
            double lead = Math.Max(3 * pitch, accelDist * 1.5 + 1);
            double depth = op.Internal ? sp.InternalDepth : sp.ExternalDepth;
            if (depth > tool.MaxDepth + 1e-6) { path.Issues.Add(Issue.Err($"{sp.Name} is {Num.F(depth)} deep; {tool.Summary} allows {Num.F(tool.MaxDepth)}.")); continue; }
            var passes = ThreadCalc.PassDepths(depth, op.FirstDepth, op.MinDepth, op.SpringPasses);
            double tanA = Math.Tan(Math.Clamp(op.InfeedAngle, 0, 30) * Math.PI / 180);
            double s = op.Internal ? -1 : 1;
            double r0 = op.Internal ? sp.InternalMinorDia / 2 : sp.MajorDia / 2;
            if (op.Internal && tool.MinBoreDia > 2 * r0) { path.Issues.Add(Issue.Err($"{tool.Summary} needs a Ø{Num.F(tool.MinBoreDia)} bore; thread minor is Ø{Num.F(2 * r0)}.")); continue; }
            double rRetract = r0 + s * clr;
            if (op.Internal) rRetract = Math.Max(r0 * 0.5, r0 - clr);
            double zStart = t.ZRight + lead;

            w.Rapid(zStart, op.Internal ? Math.Max(w.Cur.R, g.StockR + clr) : Math.Max(w.Cur.R, rRetract));
            w.Rapid(zStart, rRetract);
            path.Issues.Add(Issue.Note($"{sp.Name}: {passes.Count} passes to {Num.F(depth)} deep, lead-in {Num.F(lead)} mm."));
            foreach (var d in passes)
            {
                double z0 = zStart - d * tanA;
                double rr = r0 - s * d;
                w.Rapid(z0, rRetract);
                w.Rapid(z0, rr);
                w.Thread(new Pt(t.ZLeft, rr), pitch);
                w.Rapid(t.ZLeft, rRetract);
                w.Rapid(zStart, rRetract);
            }
            if (op.Internal) w.Rapid(zStart, rRetract);
        }
        w.Home();
    }
}

internal static class PartingOff
{
    public static void Generate(CamContext ctx, PartOffOp op, ToolDef tool, OpPath path, CamState st, CutParams cut)
    {
        var g = ctx.G;
        double clr = Math.Max(0.3, ctx.Machine.Clearance);
        double z = -g.Length;
        double tw = tool.Width;
        if (z - tw < g.JawZ + 1) path.Issues.Add(Issue.Err($"Parting tool would be within 1 mm of the jaws (cut at Z{Num.F(z - tw)}, jaws at Z{Num.F(g.JawZ)}). Increase stick-out."));
        double rStart = g.StockR + clr;
        double rEnd = op.EndDiameter / 2;
        double holeR = Math.Max(g.StockBoreR, 0);
        if (g.BoreLength >= g.Length - 1e-6 && !g.Id.IsEmpty) holeR = Math.Max(holeR, g.Id.End.R);
        if (st.DrillDepth >= g.Length) holeR = Math.Max(holeR, st.HoleR);
        if (holeR > 0) rEnd = Math.Max(rEnd, holeR - 0.3);
        if (g.StockR - rEnd > tool.MaxDepth) path.Issues.Add(Issue.Err($"Parting needs {Num.F(g.StockR - rEnd)} reach; {tool.Summary} has {Num.F(tool.MaxDepth)}."));
        double slowR = g.StockR * Math.Clamp(op.SlowBelowPercent, 0, 100) / 100;
        double peck = op.PeckDepth > 0 ? op.PeckDepth : double.MaxValue;

        var w = new PathWriter(path);
        w.Rapid(w.Cur.Z, rStart);
        w.Rapid(z, rStart);
        double r = g.StockR;
        w.Rapid(z, r + 0.2);
        while (r > rEnd + 1e-6)
        {
            double next = Math.Max(rEnd, r - peck);
            if (r > slowR && next < slowR) next = Math.Max(rEnd, slowR);
            double f = next < slowR - 1e-6 || r <= slowR + 1e-6 ? cut.Feed * Math.Clamp(op.SlowFeedFactor, 0.1, 1) : cut.Feed;
            w.Feed(z, next, f);
            r = next;
            if (r > rEnd + 1e-6) { w.Rapid(z, r + 0.3); w.Feed(z, r, f); }
        }
        w.Rapid(z, rStart);
        w.Home();
        path.Issues.Add(Issue.Note("The part drops free at the end of part-off; keep hands clear until the spindle stops."));
    }
}
