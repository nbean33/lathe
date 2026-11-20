using System.Collections.ObjectModel;
using Lathe.Core.Geometry;
using Lathe.Core.Threads;

namespace Lathe.Core.Model;

/// <summary>Raw bar stock held in the chuck.</summary>
public sealed class Stock : ModelBase
{
    private double _dia = 25, _stickOut = 60, _faceAllow = 1, _bore;
    public double Diameter { get => _dia; set => Set(ref _dia, value); }
    /// <summary>Length of stock protruding from the chuck jaws.</summary>
    public double StickOut { get => _stickOut; set => Set(ref _stickOut, value); }
    /// <summary>Material to face off the right end (stock face sits at Z = +FaceAllowance).</summary>
    public double FaceAllowance { get => _faceAllow; set => Set(ref _faceAllow, value); }
    /// <summary>Inside diameter for tube stock (0 = solid bar).</summary>
    public double BoreDiameter { get => _bore; set => Set(ref _bore, value); }
    public override string Summary => $"Ø{Num.F(Diameter)} bar, {Num.F(StickOut)} out of the chuck";
    /// <summary>Z of the chuck jaw face in work coordinates.</summary>
    public double JawZ => FaceAllowance - StickOut;
}

/// <summary>The part, described by simple features. Z0 is the finished right face.</summary>
public sealed class PartModel : ModelBase
{
    private bool _partOff = true;
    public ObservableCollection<Feature> OdFeatures { get; set; } = new();
    public ObservableCollection<Feature> IdFeatures { get; set; } = new();
    public ObservableCollection<Groove> Grooves { get; set; } = new();
    public ObservableCollection<ThreadFeature> Threads { get; set; } = new();
    /// <summary>True if the part is cut off the bar at its left end.</summary>
    public bool PartOff { get => _partOff; set => Set(ref _partOff, value); }
}

/// <summary>Resolved geometry derived from the feature lists.</summary>
public sealed class PartGeometry
{
    /// <summary>Outside profile from (0,0) up the face, then leftward to Z = -Length.</summary>
    public Profile Od { get; init; } = new();
    /// <summary>Bore profile from the face inward (empty if solid).</summary>
    public Profile Id { get; init; } = new();
    public double Length { get; init; }
    public double BoreLength => Id.IsEmpty ? 0 : -Id.End.Z;
    public double StockR { get; init; }
    public double StockBoreR { get; init; }
    public double StockFaceZ { get; init; }
    public double JawZ { get; init; }
    public bool PartOff { get; init; }
    public List<(Groove G, double ZRight, double ZLeft, double R)> Grooves { get; init; } = new();
    public List<(ThreadFeature T, ThreadSpec Spec, double ZRight, double ZLeft)> Threads { get; init; } = new();
    public List<Issue> Issues { get; init; } = new();
    public bool Ok => !Issues.HasErrors();
}

public static class PartBuilder
{
    private const double Eps = 1e-6;

    public static PartGeometry Build(Stock stock, PartModel part)
    {
        var issues = new List<Issue>();
        double stockR = stock.Diameter / 2;

        if (stock.Diameter <= 0) issues.Add(Issue.Err("Stock diameter must be greater than zero.", "Stock"));
        if (stock.StickOut <= 0) issues.Add(Issue.Err("Stick-out must be greater than zero.", "Stock"));
        if (stock.FaceAllowance < 0 || stock.FaceAllowance > 10) issues.Add(Issue.Err("Face allowance must be between 0 and 10 mm.", "Stock"));
        if (stock.BoreDiameter < 0 || stock.BoreDiameter >= stock.Diameter) issues.Add(Issue.Err("Tube bore must be smaller than the stock diameter.", "Stock"));
        if (part.OdFeatures.Count == 0) issues.Add(Issue.Err("Add at least one outside feature (diameter, taper or arc).", "Part"));

        var od = BuildChain(part.OdFeatures, new Pt(0, 0), isId: false, stockR, issues, "OD");
        double length = od.IsEmpty ? 0 : -od.End.Z;

        var id = new Profile();
        if (part.IdFeatures.Count > 0)
            id = BuildChain(part.IdFeatures, new Pt(0, 0), isId: true, stockR, issues, "Bore");

        var g = new PartGeometry
        {
            Od = od, Id = id, Length = length, StockR = stockR, StockBoreR = stock.BoreDiameter / 2,
            StockFaceZ = stock.FaceAllowance, JawZ = stock.JawZ, PartOff = part.PartOff, Issues = issues,
        };

        if (!od.IsEmpty)
        {
            if (length + 2 > stock.StickOut - stock.FaceAllowance)
                issues.Add(Issue.Err($"Part is {Num.F(length)} long but only {Num.F(stock.StickOut - stock.FaceAllowance - 2)} is usable outside the jaws (keep 2 mm clearance). Increase stick-out.", "Stock"));
            CheckMonotonic(od, "OD", issues);
            foreach (var p in od.ToPolyline())
                if (p.R > stockR + Eps) { issues.Add(Issue.Err($"OD profile is larger than the stock (Ø{Num.F(2 * p.R)} > Ø{Num.F(stock.Diameter)}).", "OD")); break; }
        }

        if (!id.IsEmpty && !od.IsEmpty)
        {
            CheckMonotonic(id, "Bore", issues);
            foreach (var p in id.ToPolyline(0.01))
            {
                double? odR = p.Z >= -length ? od.RadiusAt(p.Z) : stockR;
                if (odR is double r && p.R > r - 0.5) { issues.Add(Issue.Err($"Wall thinner than 0.5 mm at Z{Num.F(p.Z)}.", "Bore")); break; }
                if (p.R < g.StockBoreR - Eps) { issues.Add(Issue.Err("Bore is smaller than the tube stock's bore.", "Bore")); break; }
            }
            if (g.BoreLength > length && !part.PartOff)
                issues.Add(Issue.Warn("Bore is longer than the part but part-off is off; it will run into the remaining stock.", "Bore"));
        }

        // Grooves
        foreach (var gr in part.Grooves)
        {
            double zr = -gr.Position, zl = -gr.Position - gr.Width, r = gr.Diameter / 2;
            if (gr.Width <= 0 || gr.Diameter <= 0) { issues.Add(Issue.Err("Groove width and diameter must be positive.", gr.Summary)); continue; }
            if (zr > Eps || zl < -length - Eps) { issues.Add(Issue.Err("Groove lies outside the part.", gr.Summary)); continue; }
            for (double z = zr; z >= zl - Eps; z -= Math.Max(gr.Width / 8, 0.05))
            {
                var pr = od.RadiusAt(z);
                if (pr is null || pr.Value <= r + Eps) { issues.Add(Issue.Err("Groove must be smaller than the diameter it is cut into.", gr.Summary)); break; }
                var ir = id.IsEmpty ? null : id.RadiusAt(z);
                if (ir is double irr && r < irr + 0.5) { issues.Add(Issue.Err("Groove breaks through the bore wall.", gr.Summary)); break; }
            }
            g.Grooves.Add((gr, zr, zl, r));
        }

        // Threads
        foreach (var th in part.Threads)
        {
            var spec = ThreadTable.Resolve(th.Spec, th.CustomMajorDia, th.CustomPitch);
            if (spec is null) { issues.Add(Issue.Err($"Unknown thread '{th.Spec}'.", th.Summary)); continue; }
            double zr = -th.Position, zl = -th.Position - th.Length;
            if (th.Length <= 0) { issues.Add(Issue.Err("Thread length must be positive.", th.Summary)); continue; }
            if (zr > Eps || zl < -length - Eps) { issues.Add(Issue.Err("Thread lies outside the part.", th.Summary)); continue; }
            var prof = th.Internal ? id : od;
            if (prof.IsEmpty) { issues.Add(Issue.Err("Internal thread needs a bore.", th.Summary)); continue; }
            double expected = th.Internal ? spec.InternalMinorDia : spec.MajorDia;
            var rAt = prof.RadiusAt((zr + zl) / 2);
            if (rAt is null) { issues.Add(Issue.Err("Thread is not on the profile.", th.Summary)); continue; }
            double dia = 2 * rAt.Value;
            if (Math.Abs(dia - expected) > Math.Max(0.3, 0.1 * spec.Pitch * 3))
                issues.Add(Issue.Warn($"{(th.Internal ? "Bore" : "Diameter")} is Ø{Num.F(dia)} but {spec.Name} expects about Ø{Num.F(expected)}.", th.Summary));
            if (!th.Internal && spec.ExternalMinorDia / 2 <= (id.IsEmpty ? 0 : id.RadiusAt((zr + zl) / 2) ?? 0) + 0.5)
                issues.Add(Issue.Err("Thread would break through the bore wall.", th.Summary));
            bool relief = g.Grooves.Any(x => x.ZRight >= zl - 0.01 && x.ZLeft <= zl + 0.01) || Math.Abs(zl + length) < 0.01 && part.PartOff;
            if (!relief && !th.Internal)
                issues.Add(Issue.Warn("No relief groove at the thread's left end; the tool will stop in material. Add a groove there.", th.Summary));
            g.Threads.Add((th, spec, zr, zl));
        }
        return g;
    }

    private static void CheckMonotonic(Profile p, string where, List<Issue> issues)
    {
        var pts = p.ToPolyline(0.005);
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].Z > pts[i - 1].Z + 1e-4)
            {
                issues.Add(Issue.Err($"Profile doubles back toward the face near Z{Num.F(pts[i].Z)} (undercut). The tool cannot reach it.", where));
                return;
            }
    }

    private sealed class Brk { public int Vertex; public EdgeBreak Edge = null!; public string Where = ""; }

    private static Profile BuildChain(IList<Feature> features, Pt start, bool isId, double stockR, List<Issue> issues, string where)
    {
        var segs = new List<Seg>();
        var breaks = new List<Brk>();
        var cur = start;
        if (features.Count == 0) return new Profile();

        // ID chains get a virtual face segment so a bore-entrance chamfer works like any corner.
        bool virtualFace = isId;
        if (isId)
        {
            double r1 = StartRadius(features[0]);
            cur = new Pt(0, r1 + 5);
            var first = new Pt(0, r1);
            segs.Add(new LineSeg(cur, first));
            cur = first;
        }

        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            string w = $"{where} #{i + 1} {f.TypeName}";
            if (f.Length <= 0) { issues.Add(Issue.Err("Length must be greater than zero.", w)); continue; }
            switch (f)
            {
                case CylinderFeature c:
                    if (c.Diameter <= 0) { issues.Add(Issue.Err("Diameter must be greater than zero.", w)); continue; }
                    AddMain(new Pt(cur.Z, c.Diameter / 2), new Pt(cur.Z - c.Length, c.Diameter / 2), c.StartEdge, c.EndEdge, w);
                    break;
                case TaperFeature t:
                    if (t.StartDiameter <= 0 || t.EndDiameter < 0) { issues.Add(Issue.Err("Diameters must be greater than zero.", w)); continue; }
                    AddMain(new Pt(cur.Z, t.StartDiameter / 2), new Pt(cur.Z - t.Length, t.EndDiameter / 2), t.StartEdge, t.EndEdge, w);
                    break;
                case ArcFeature a:
                {
                    var end = new Pt(cur.Z - a.Length, a.EndDiameter / 2);
                    double chord = cur.DistanceTo(end);
                    if (a.Radius < chord / 2 - 1e-6) { issues.Add(Issue.Err($"Radius must be at least {Num.F(chord / 2)} to span this arc.", w)); continue; }
                    var mid = (cur + end) * 0.5;
                    var u = (end - cur).Normalized();
                    var nL = u.Perp();
                    double h = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - chord * chord / 4));
                    // Convex = bulges away from the material side's interior (up for OD, down for ID).
                    bool bulgeUp = isId ? !a.Convex : a.Convex;
                    // Travelling leftward, "up" is to the right of travel, so the center is on the left: a CCW arc.
                    var center = bulgeUp ? mid + nL * h : mid - nL * h;
                    segs.Add(new ArcSeg(cur, end, center, Ccw: bulgeUp));
                    cur = end;
                    break;
                }
            }
        }

        void AddMain(Pt a, Pt b, EdgeBreak sEdge, EdgeBreak eEdge, string w)
        {
            if (cur.DistanceTo(a) > Eps) segs.Add(new LineSeg(cur, a));
            if (sEdge.IsSet)
            {
                if (segs.Count == 0) issues.Add(Issue.Warn("Edge break at the very start has no corner; ignored.", w));
                else breaks.Add(new Brk { Vertex = segs.Count, Edge = sEdge, Where = w + " right edge" });
            }
            segs.Add(new LineSeg(a, b));
            if (eEdge.IsSet) breaks.Add(new Brk { Vertex = segs.Count, Edge = eEdge, Where = w + " left edge" });
            cur = b;
        }

        // Apply corner breaks from right to left.
        foreach (var b in breaks.OrderByDescending(b => b.Vertex))
        {
            int k = b.Vertex;
            if (k >= segs.Count) { issues.Add(Issue.Warn("Left edge of the last feature can't be broken here (do it after part-off); ignored.", b.Where)); continue; }
            if (segs[k - 1] is not LineSeg sin || segs[k] is not LineSeg sout) { issues.Add(Issue.Err("Chamfers and radii can only join straight segments.", b.Where)); continue; }
            var v = sin.B;
            if (Math.Abs((v - sin.A).Normalized().Cross((sout.B - v).Normalized())) < 1e-6) { issues.Add(Issue.Warn("No corner here (segments are in line); ignored.", b.Where)); continue; }
            double need = b.Edge.Kind == EdgeKind.Chamfer ? b.Edge.Size : Corner.TangentLength(sin.A, v, sout.B, b.Edge.Size);
            if (need > sin.Length + 1e-6 || need > sout.Length + 1e-6) { issues.Add(Issue.Err($"{b.Edge.Summary} is too big for the adjoining segments.", b.Where)); continue; }
            if (b.Edge.Kind == EdgeKind.Chamfer)
            {
                var (p1, p2) = Corner.Chamfer(sin.A, v, sout.B, b.Edge.Size);
                segs[k - 1] = sin with { B = p1 };
                segs[k] = sout with { A = p2 };
                segs.Insert(k, new LineSeg(p1, p2));
            }
            else
            {
                var fil = Corner.Fillet(sin.A, v, sout.B, b.Edge.Size);
                if (fil is null) continue;
                segs[k - 1] = sin with { B = fil.Value.a };
                segs[k] = sout with { A = fil.Value.b };
                segs.Insert(k, fil.Value.arc);
            }
        }

        // Drop zero-length leftovers and the virtual ID face.
        var result = segs.Where(s => s.Length > 1e-7).ToList();
        if (virtualFace && result.Count > 0 && result[0] is LineSeg l0 && Math.Abs(l0.A.Z) < Eps && Math.Abs(l0.B.Z) < Eps)
            result.RemoveAt(0);
        return new Profile(result);
    }

    private static double StartRadius(Feature f) => f switch
    {
        CylinderFeature c => c.Diameter / 2,
        TaperFeature t => t.StartDiameter / 2,
        ArcFeature a => a.EndDiameter / 2,
        _ => 0,
    };
}
