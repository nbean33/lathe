using Clipper2Lib;
using Lathe.Core.Geometry;
using Lathe.Core.Tools;

namespace Lathe.Cam;

/// <summary>Tool outline relative to its control point, in real (Z, R) coordinates.</summary>
public sealed record ToolShape(PathD Cutting, PathD Holder)
{
    public PathD All { get; } = MergeOutline(Cutting, Holder);

    private static PathD MergeOutline(PathD a, PathD b)
    {
        var u = Geo.Union(new PathsD { a, b });
        return u.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
    }
}

public static class ToolShapes
{
    public static ToolShape For(ToolDef t) => t.Type switch
    {
        ToolType.Turning => Turning(t),
        ToolType.Boring => Mirror(TurningLike(t, boringBar: true)),
        ToolType.Grooving or ToolType.PartOff => Blade(t),
        ToolType.ThreadExternal => Thread(t, inside: false),
        ToolType.ThreadInternal => Mirror(Thread(t, inside: true)),
        ToolType.Drill or ToolType.CenterDrill => Drill(t),
        _ => Turning(t),
    };

    private static ToolShape Mirror(ToolShape s) => new(Geo.Mirror(s.Cutting), Geo.Mirror(s.Holder));

    private static ToolShape Turning(ToolDef t) => TurningLike(t, boringBar: false);

    /// <summary>OD-style insert: nose at bottom-left, control point = (leftmost, lowest) of the nose.</summary>
    private static ToolShape TurningLike(ToolDef t, bool boringBar)
    {
        double r = Math.Max(0.01, t.NoseRadius);
        var c = new Pt(r, r);
        PathD insert;
        if (t.Shape == InsertShape.Round)
        {
            double rr = Math.Max(r, t.InsertSize / 2);
            insert = Clipper.Ellipse(new PointD(rr, rr), rr, rr, 48);
        }
        else
        {
            double eps = t.IncludedAngle, a1 = 180 - t.ApproachAngle, a2 = a1 - eps;
            var d1 = Pt.FromAngle(a1); var d2 = Pt.FromAngle(a2);
            var bis = Pt.FromAngle((a1 + a2) / 2);
            var tip = c - bis * (r / Math.Sin(eps / 2 * Math.PI / 180));
            double L = Math.Max(3, t.InsertSize);
            var pts = t.Shape == InsertShape.T60
                ? new[] { tip, tip + d1 * L, tip + d2 * L }
                : new[] { tip, tip + d1 * L, tip + (d1 + d2) * L, tip + d2 * L };
            var sharp = Geo.One(Geo.Path(pts));
            if (Clipper.Area(sharp) < 0) sharp = Clipper.ReversePaths(sharp);
            var rounded = Geo.Inflate(Geo.Inflate(sharp, -r), r);
            insert = rounded.Count > 0 ? rounded[0] : sharp[0];
        }
        var (zMin, zMax, rMin, rMax) = Geo.Bounds(Geo.One(insert));
        PathD holder = boringBar
            // Boring bar (drawn OD-style, mirrored later): shank runs to the right, toward the axis.
            ? Geo.Rect(zMin + (zMax - zMin) * 0.5, zMax + 250, rMin + Math.Min(0.8, (rMax - rMin) * 0.3), rMin + Math.Max(t.Diameter, (rMax - rMin)))
            : Geo.Rect(zMin + (zMax - zMin) * 0.55, zMax + 4, rMin + (rMax - rMin) * 0.55, rMax + 40);
        return new ToolShape(insert, holder);
    }

    /// <summary>Grooving/parting blade: control point = front edge, right side.</summary>
    private static ToolShape Blade(ToolDef t)
    {
        double w = t.Width, d = t.MaxDepth;
        return new ToolShape(Geo.Rect(-w, 0, 0, d), Geo.Rect(-w - 1, 6, d, d + 30));
    }

    /// <summary>60° threading tool, control point = tip. Internal version is built OD-style and mirrored.</summary>
    private static ToolShape Thread(ToolDef t, bool inside)
    {
        double h = Math.Max(2, t.MaxDepth > 5 ? 3 : t.MaxDepth + 1);
        double s = Math.Tan(30 * Math.PI / 180) * h;
        var tri = Geo.Path(new[] { new Pt(0, 0), new Pt(s, h), new Pt(-s, h) });
        if (Clipper.Area(tri) < 0) tri = Clipper.ReversePath(tri);
        var holder = inside
            ? Geo.Rect(-s * 0.5, 250, h * 0.6, Math.Max(h + 0.5, t.Diameter))
            : Geo.Rect(-s - 1, s + 8, h, h + 30);
        return new ToolShape(tri, holder);
    }

    /// <summary>Drill: control point = tip on the centerline.</summary>
    private static ToolShape Drill(ToolDef t)
    {
        double rad = t.Diameter / 2, hc = rad / Math.Tan(t.PointAngle / 2 * Math.PI / 180);
        double flute = t.MaxDepth + hc;
        var body = Geo.Path(new[] { new Pt(0, 0), new Pt(hc, rad), new Pt(flute, rad), new Pt(flute, -rad), new Pt(hc, -rad) });
        if (Clipper.Area(body) < 0) body = Clipper.ReversePath(body);
        double chuckR = Math.Max(rad + 3, 15);
        return new ToolShape(body, Geo.Rect(flute, flute + 60, -chuckR, chuckR));
    }

    /// <summary>Axial length from the drill tip to the start of the full diameter.</summary>
    public static double DrillPointLength(ToolDef t) => t.Diameter / 2 / Math.Tan(t.PointAngle / 2 * Math.PI / 180);
}
