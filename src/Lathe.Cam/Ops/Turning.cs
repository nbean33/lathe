using Clipper2Lib;
using Lathe.Core;
using Lathe.Core.Geometry;
using Lathe.Core.Model;
using Lathe.Core.Tools;

namespace Lathe.Cam.Ops;

/// <summary>
/// OD/ID roughing and finishing. Inside work is done in a mirrored "frame" (R → -R) so both sides
/// share one algorithm: material lies below the profile, free space above.
/// All collision avoidance uses the configuration-space obstacle of the real tool outline.
/// </summary>
internal static class Turning
{
    public static void Generate(CamContext ctx, TurnOp op, ToolDef tool, OpPath path, CamState st, CutParams cut)
    {
        bool inside = op.Side == TurnSide.Inside;
        double s = inside ? -1 : 1;
        var g = ctx.G;
        double clr = Math.Max(0.3, ctx.Machine.Clearance);
        double zRightMat = st.Faced ? 0 : g.StockFaceZ;
        double zEntry = zRightMat + clr;
        double yTop, ySafe;

        if (inside)
        {
            if (g.Id.IsEmpty) { path.Issues.Add(Issue.Err("ID operation but the part has no bore features.")); return; }
            double holeR = st.HoleR;
            if (holeR <= 0) { path.Issues.Add(Issue.Err("Boring needs a hole first: add a Drill operation before this one (or use tube stock).")); return; }
            if (tool.MinBoreDia > 2 * holeR + 1e-6) { path.Issues.Add(Issue.Err($"{tool.Summary} needs at least a Ø{Num.F(tool.MinBoreDia)} hole; the drilled hole is Ø{Num.F(2 * holeR)}.")); return; }
            if (g.BoreLength > tool.MaxDepth + 1e-6) { path.Issues.Add(Issue.Err($"Bore is {Num.F(g.BoreLength)} deep but {tool.Summary} reaches only {Num.F(tool.MaxDepth)}.")); return; }
            yTop = -holeR;
            ySafe = -Math.Max(holeR * 0.5, holeR - clr);
        }
        else
        {
            yTop = g.StockR;
            ySafe = g.StockR + clr;
        }

        var obsReal = inside ? ctx.IdObstacle(zRightMat) : ctx.OdObstacle;
        var obs = inside ? Geo.Mirror(obsReal) : obsReal;
        var shape = ToolShapes.For(tool).All;
        var toolFrame = inside ? Geo.Mirror(shape) : shape;
        double allow = op.Stage == TurnStage.Rough ? Math.Max(0, op.Allowance) : 0;
        var K = Geo.ConfigObstacle(Geo.Inflate(obs, allow), toolFrame);

        // Where the profile chain starts on the face.
        double yStartFace;
        if (inside) yStartFace = -g.Id.Start.R - 0.5;
        else yStartFace = g.Od.Segments[0] is LineSeg l0 && Math.Abs(l0.A.Z) < 1e-6 && Math.Abs(l0.B.Z) < 1e-6 ? l0.B.R - 0.5 : -1;
        double yLow = inside ? -(g.Id.ToPolyline(0.05).Max(p => p.R) + 3) : -1;
        double zLeft = g.JawZ - 5;

        var chain = ExtractChain(K, zLeft, zEntry, yLow, ySafe, yStartFace, ctx.Tol);
        if (chain.Count < 2) { path.Issues.Add(Issue.Err("Could not find a toolpath along the profile (tool may not fit).")); return; }

        var w = new PathWriter(path);
        Pt R(Pt f) => new(f.Z, s * f.R);
        Pt cur = new(zEntry, ySafe);
        w.Rapid(path.Start.Z, Math.Max(path.Start.R, inside ? 0 : ySafe));
        w.Rapid(R(cur));

        void GoEntry(double y)
        {
            if (Math.Abs(cur.Z - zEntry) > 1e-6) { w.Rapid(R(new Pt(cur.Z, ySafe))); w.Rapid(R(new Pt(zEntry, ySafe))); }
            cur = new Pt(zEntry, y); w.Rapid(R(cur));
        }

        if (op.Stage == TurnStage.Rough)
        {
            double doc = Math.Max(0.05, op.DepthOfCut > 0 ? op.DepthOfCut : cut.Doc);
            double yFloor = chain.Min(p => p.R);
            var skipped = new List<(double a, double b)>();
            bool warnedNoPocket = false, warnedShort = false;
            double prevY = yTop;
            for (int k = 1; k < 5000; k++)
            {
                double y = yTop - k * doc;
                bool last = false;
                if (y <= yFloor + 0.001) { y = yFloor + 0.001; last = true; }
                if (y >= prevY - 1e-4) break;
                double step = prevY - y;
                foreach (var (a0, b0) in Intervals(K, y, zEntry, zLeft))
                {
                    double a = a0, b = Math.Min(b0, zEntry);
                    if (b - a < 0.05 || a >= zRightMat - 0.01) continue;
                    bool open = b >= zEntry - 1e-4;
                    double d = Math.Min(0.5, Math.Min(step, (b - a) / 2));
                    if (open)
                    {
                        GoEntry(y);
                        w.Feed(R(new Pt(a, y)), cut.Feed);
                        w.Feed(R(new Pt(a + d, y + d)), cut.Feed);
                        cur = new Pt(zEntry, y + d); w.Rapid(R(cur));
                        continue;
                    }
                    // Hollow (pocket): ramp in from its right wall if the tool allows it.
                    if (skipped.Any(sk => a < sk.b && b > sk.a)) { skipped.Add((a, b)); continue; }
                    double ang = Math.Min(tool.MaxRampAngle, 45);
                    if (!op.AllowPockets || ang < 1)
                    {
                        skipped.Add((a, b));
                        if (!warnedNoPocket) { path.Issues.Add(Issue.Warn($"Hollow near Z{Num.F(b)} is not roughed ({(op.AllowPockets ? tool.Summary + " can't ramp down" : "pockets disabled")}); the finish pass will take it in one cut or leave it.")); warnedNoPocket = true; }
                        continue;
                    }
                    double ramp = step / Math.Tan(ang * Math.PI / 180);
                    if (b - ramp < a + 0.05 || Geo.SegmentHits(K, new Pt(b, prevY), new Pt(b - ramp, y)))
                    {
                        skipped.Add((a, b));
                        if (!warnedShort) { path.Issues.Add(Issue.Warn($"Hollow near Z{Num.F(b)} is too short to ramp into with {tool.Summary}; left for the finish pass.")); warnedShort = true; }
                        continue;
                    }
                    w.Rapid(R(new Pt(cur.Z, ySafe)));
                    w.Rapid(R(new Pt(b, ySafe)));
                    w.Rapid(R(new Pt(b, prevY + 0.1)));
                    w.Feed(R(new Pt(b, prevY)), cut.Feed);
                    w.Feed(R(new Pt(b - ramp, y)), cut.Feed);
                    w.Feed(R(new Pt(a, y)), cut.Feed);
                    w.Feed(R(new Pt(a + d, y + d)), cut.Feed);
                    cur = new Pt(a + d, ySafe); w.Rapid(R(cur));
                }
                prevY = y;
                if (last) break;
            }
        }

        // Profile pass: semi-finish (rough, at allowance) or finish.
        GoEntry(chain[0].R);
        foreach (var p in chain) w.Feed(R(p), cut.FinishFeed);
        cur = chain[^1];
        if (cur.R < ySafe) { cur = new Pt(cur.Z + Math.Min(0.3, clr), ySafe); w.Rapid(R(cur)); }
        w.Rapid(R(new Pt(zEntry, ySafe)));
        w.Home();
    }

    public static List<(double a, double b)> Intervals(PathsD K, double y, double zRight, double zLeft)
    {
        var outside = Geo.ClipOpen(new[] { new Pt(zRight, y), new Pt(zLeft, y) }, K, keepInside: false);
        return outside.Select(p => (a: p.Min(q => q.Z), b: p.Max(q => q.Z)))
            .Where(iv => iv.b - iv.a > 1e-3).OrderByDescending(iv => iv.b).ToList();
    }

    /// <summary>
    /// The part of K's boundary the tool rides along: from the face (just below the first corner) leftward
    /// and up the last wall to yHigh. Returned right-to-left.
    /// </summary>
    public static List<Pt> ExtractChain(PathsD K, double zLeft, double zRight, double yLow, double yHigh, double yStartFace, double tol)
    {
        var region = Geo.One(Geo.Rect(zLeft, zRight, yLow, yHigh));
        if (yStartFace > yLow) region = Geo.Diff(region, Geo.One(Geo.Rect(-0.02, zRight + 1, yLow - 1, yStartFace)));
        var pieces = new List<List<Pt>>();
        foreach (var ring in K)
        {
            if (ring.Count < 3) continue;
            int iMin = 0;
            for (int i = 1; i < ring.Count; i++) if (ring[i].y < ring[iMin].y) iMin = i;
            var loop = new List<Pt>();
            for (int i = 0; i <= ring.Count; i++) loop.Add(Geo.P(ring[(iMin + i) % ring.Count]));
            pieces.AddRange(Geo.ClipOpen(loop, region, keepInside: true));
        }
        if (pieces.Count == 0) return new();
        var best = pieces.OrderByDescending(Geo.PolyLen).First();
        if (best[0].Z < best[^1].Z) best.Reverse();
        return Geo.Simplify(best, Math.Max(tol * 0.5, 0.0005));
    }
}
