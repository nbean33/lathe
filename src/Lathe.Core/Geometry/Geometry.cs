namespace Lathe.Core.Geometry;

/// <summary>
/// A point in the lathe's half-section plane. Z runs along the spindle axis (Z0 = finished face,
/// negative toward the chuck). R is the RADIUS from the spindle centerline (never diameter).
/// </summary>
public readonly record struct Pt(double Z, double R)
{
    public static Pt operator +(Pt a, Pt b) => new(a.Z + b.Z, a.R + b.R);
    public static Pt operator -(Pt a, Pt b) => new(a.Z - b.Z, a.R - b.R);
    public static Pt operator *(Pt a, double s) => new(a.Z * s, a.R * s);
    public static Pt operator *(double s, Pt a) => new(a.Z * s, a.R * s);
    public double Length => Math.Sqrt(Z * Z + R * R);
    public Pt Normalized() { var l = Length; return l < 1e-12 ? new Pt(0, 0) : new Pt(Z / l, R / l); }
    public double Dot(Pt o) => Z * o.Z + R * o.R;
    public double Cross(Pt o) => Z * o.R - R * o.Z;
    /// <summary>Rotate 90 degrees counter-clockwise (Z right, R up).</summary>
    public Pt Perp() => new(-R, Z);
    public double DistanceTo(Pt o) => (this - o).Length;
    public Pt MirrorR() => new(Z, -R);
    public static Pt FromAngle(double degrees) { var a = degrees * Math.PI / 180; return new(Math.Cos(a), Math.Sin(a)); }
    public override string ToString() => $"(Z{Z:0.###}, R{R:0.###})";
}

/// <summary>A profile segment: a straight line or a circular arc.</summary>
public abstract record Seg(Pt A, Pt B)
{
    public abstract double Length { get; }
    /// <summary>Points along the segment, excluding A and including B, with chord error below tol.</summary>
    public abstract IEnumerable<Pt> Sample(double tol);
}

public sealed record LineSeg(Pt A, Pt B) : Seg(A, B)
{
    public override double Length => A.DistanceTo(B);
    public override IEnumerable<Pt> Sample(double tol) { yield return B; }
}

/// <summary>Circular arc from A to B around C. Ccw is measured with Z pointing right and R up (G3 in G18).</summary>
public sealed record ArcSeg(Pt A, Pt B, Pt C, bool Ccw) : Seg(A, B)
{
    public double Radius => C.DistanceTo(A);
    public double Sweep
    {
        get
        {
            double a0 = Math.Atan2(A.R - C.R, A.Z - C.Z), a1 = Math.Atan2(B.R - C.R, B.Z - C.Z);
            double s = a1 - a0;
            if (Ccw) { while (s <= 0) s += 2 * Math.PI; } else { while (s >= 0) s -= 2 * Math.PI; }
            return s;
        }
    }
    public override double Length => Math.Abs(Sweep) * Radius;
    public override IEnumerable<Pt> Sample(double tol)
    {
        double r = Radius, sweep = Sweep;
        double maxStep = r <= tol ? Math.PI / 2 : 2 * Math.Acos(Math.Max(-1, 1 - tol / r));
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(maxStep, 1e-3)));
        double a0 = Math.Atan2(A.R - C.R, A.Z - C.Z);
        for (int i = 1; i < n; i++)
        {
            double a = a0 + sweep * i / n;
            yield return new Pt(C.Z + r * Math.Cos(a), C.R + r * Math.Sin(a));
        }
        yield return B;
    }
}

/// <summary>A connected chain of segments.</summary>
public sealed class Profile
{
    public List<Seg> Segments { get; } = new();
    public Profile() { }
    public Profile(IEnumerable<Seg> segs) => Segments.AddRange(segs);
    public bool IsEmpty => Segments.Count == 0;
    public Pt Start => Segments[0].A;
    public Pt End => Segments[^1].B;

    public List<Pt> ToPolyline(double tol = 0.002)
    {
        var pts = new List<Pt>();
        if (IsEmpty) return pts;
        pts.Add(Start);
        foreach (var s in Segments)
            foreach (var p in s.Sample(tol))
                if (pts[^1].DistanceTo(p) > 1e-9) pts.Add(p);
        return pts;
    }

    /// <summary>
    /// Radius of the profile at a given Z (polyline approximation); the smallest if several sloped segments
    /// meet there. Vertical steps are only used when nothing else covers Z. Null if Z is outside.
    /// </summary>
    public double? RadiusAt(double z, double tol = 0.002)
    {
        var pts = ToPolyline(tol);
        double? best = null, vertical = null;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double lo = Math.Min(a.Z, b.Z), hi = Math.Max(a.Z, b.Z);
            if (z < lo - 1e-9 || z > hi + 1e-9) continue;
            if (hi - lo < 1e-9) { vertical = Math.Max(vertical ?? double.MinValue, Math.Max(a.R, b.R)); continue; }
            double r = a.R + (b.R - a.R) * (z - a.Z) / (b.Z - a.Z);
            best = best is null ? r : Math.Min(best.Value, r);
        }
        return best ?? vertical;
    }
}

public static class Corner
{
    /// <summary>
    /// Replace the corner V between two lines (P0->V, V->P1) with a chamfer (distance c along each leg).
    /// Returns the two trim points.
    /// </summary>
    public static (Pt a, Pt b) Chamfer(Pt p0, Pt v, Pt p1, double c)
    {
        var u = (v - p0).Normalized(); var w = (p1 - v).Normalized();
        return (v - u * c, v + w * c);
    }

    /// <summary>Tangent fillet of radius rho at corner V. Returns trim points and the arc.</summary>
    public static (Pt a, Pt b, ArcSeg arc)? Fillet(Pt p0, Pt v, Pt p1, double rho)
    {
        var u = (v - p0).Normalized(); var w = (p1 - v).Normalized();
        double cross = u.Cross(w);
        if (Math.Abs(cross) < 1e-9) return null; // collinear
        double cosT = Math.Clamp((u * -1).Dot(w), -1, 1);
        double theta = Math.Acos(cosT);            // interior angle between legs
        double t = rho / Math.Tan(theta / 2);
        var a = v - u * t; var b = v + w * t;
        bool left = cross > 0;
        var n = left ? u.Perp() : u.Perp() * -1;
        var c = a + n * rho;
        return (a, b, new ArcSeg(a, b, c, left));
    }

    public static double TangentLength(Pt p0, Pt v, Pt p1, double rho)
    {
        var u = (v - p0).Normalized(); var w = (p1 - v).Normalized();
        double theta = Math.Acos(Math.Clamp((u * -1).Dot(w), -1, 1));
        return Math.Abs(Math.Tan(theta / 2)) < 1e-12 ? double.PositiveInfinity : rho / Math.Tan(theta / 2);
    }
}
