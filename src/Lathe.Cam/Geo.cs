using Clipper2Lib;
using Lathe.Core.Geometry;

namespace Lathe.Cam;

/// <summary>Thin wrapper over Clipper2 with a fixed 0.0001 mm precision. X = Z axis, Y = radius.</summary>
public static class Geo
{
    public const int Prec = 4;

    public static PathD Path(IEnumerable<Pt> pts) => new(pts.Select(p => new PointD(p.Z, p.R)));
    public static Pt P(PointD p) => new(p.x, p.y);
    public static List<Pt> Pts(PathD p) => p.Select(P).ToList();
    public static PathsD One(PathD p) => new() { p };

    public static PathD Rect(double z0, double z1, double r0, double r1) => new()
    {
        new PointD(Math.Min(z0, z1), Math.Min(r0, r1)), new PointD(Math.Max(z0, z1), Math.Min(r0, r1)),
        new PointD(Math.Max(z0, z1), Math.Max(r0, r1)), new PointD(Math.Min(z0, z1), Math.Max(r0, r1)),
    };

    public static PathsD Union(PathsD a) => Clipper.Union(a, new PathsD(), FillRule.NonZero, Prec);
    public static PathsD Union(PathsD a, PathsD b) => Clipper.Union(a, b, FillRule.NonZero, Prec);
    public static PathsD Diff(PathsD a, PathsD b) => Clipper.Difference(a, b, FillRule.NonZero, Prec);
    public static PathsD Inter(PathsD a, PathsD b) => Clipper.Intersect(a, b, FillRule.NonZero, Prec);
    public static double Area(PathsD a) => Math.Abs(Clipper.Area(a));

    public static PathsD Inflate(PathsD a, double delta) =>
        Math.Abs(delta) < 1e-9 ? a : Clipper.InflatePaths(a, delta, JoinType.Round, EndType.Polygon, 2, Prec, 0.0005);

    public static PathsD Mirror(PathsD a) => new(a.Select(p => new PathD(p.Select(q => new PointD(q.x, -q.y)).Reverse())));
    public static PathD Mirror(PathD p) => new(p.Select(q => new PointD(q.x, -q.y)).Reverse());
    /// <summary>Region plus its mirror about the spindle axis (a full section).</summary>
    public static PathsD Full(PathsD upper) => Union(upper, Mirror(upper));
    public static PathsD Translate(PathsD a, Pt d) => Clipper.TranslatePaths(a, d.Z, d.R);
    public static PathD Translate(PathD a, Pt d) => Clipper.TranslatePath(a, d.Z, d.R);
    public static PathD Negate(PathD p) => new(p.Select(q => new PointD(-q.x, -q.y)));

    /// <summary>Area covered by `shape` as its reference point moves from a to b.</summary>
    public static PathsD Sweep(PathD shape, Pt a, Pt b)
    {
        var start = Translate(shape, a);
        if (a.DistanceTo(b) < 1e-7) return One(start);
        var sum = Minkowski.Sum(shape, new PathD { new PointD(a.Z, a.R), new PointD(b.Z, b.R) }, false, Prec);
        sum.Add(start); sum.Add(Translate(shape, b));
        return Union(sum);
    }

    /// <summary>
    /// Configuration-space obstacle: the set of reference-point positions where the tool shape overlaps the region.
    /// </summary>
    public static PathsD ConfigObstacle(PathsD region, PathD toolShape)
    {
        var neg = Negate(toolShape);
        var parts = new PathsD();
        foreach (var ring in region)
            parts.AddRange(Minkowski.Sum(neg, ring, true, Prec));
        var q = P(neg[0]);
        parts.AddRange(Translate(region, q));
        return Union(parts);
    }

    /// <summary>Open polyline clipped against a closed region. Returns the pieces inside (Intersection) or outside (Difference).</summary>
    public static List<List<Pt>> ClipOpen(IEnumerable<Pt> line, PathsD region, bool keepInside)
    {
        var c = new ClipperD(Prec);
        c.AddOpenSubject(Path(line));
        c.AddClip(region);
        var closed = new PathsD(); var open = new PathsD();
        c.Execute(keepInside ? ClipType.Intersection : ClipType.Difference, FillRule.NonZero, closed, open);
        return open.Select(Pts).ToList();
    }

    public static bool SegmentHits(PathsD region, Pt a, Pt b)
    {
        var inside = ClipOpen(new[] { a, b }, region, true);
        return inside.Any(p => p.Count >= 2 && PolyLen(p) > 1e-3);
    }

    public static double PolyLen(IReadOnlyList<Pt> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += p[i].DistanceTo(p[i - 1]); return s; }

    public static (double zMin, double zMax, double rMin, double rMax) Bounds(PathsD a)
    {
        var b = Clipper.GetBounds(a);
        return (b.left, b.right, b.top, b.bottom);
    }

    public static List<Pt> Simplify(List<Pt> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var s = Clipper.RamerDouglasPeucker(Path(pts), tol);
        return Pts(s);
    }
}
