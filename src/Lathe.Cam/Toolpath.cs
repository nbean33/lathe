using Clipper2Lib;
using Lathe.Core;
using Lathe.Core.Geometry;
using Lathe.Core.Model;
using Lathe.Core.Project;
using Lathe.Core.Tools;

namespace Lathe.Cam;

public enum MoveKind { Rapid, Feed, Thread }

/// <summary>A move of the tool control point to `To` (real Z/R in mm). Feed is mm/rev; Pitch is mm/rev for threads.</summary>
public readonly record struct Move(MoveKind Kind, Pt To, double Feed = 0, double Pitch = 0);

/// <summary>The toolpath of one operation. It starts and ends at the tool-change position.</summary>
public sealed class OpPath
{
    public int OpIndex { get; init; }
    public string Name { get; init; } = "";
    public ToolDef Tool { get; init; } = null!;
    public int Rpm { get; set; }
    public bool Coolant { get; init; }
    public Pt Start { get; init; }
    public List<Move> Moves { get; } = new();
    public List<Issue> Issues { get; } = new();
    public double Seconds { get; set; }
}

/// <summary>Incremental toolpath writer that drops zero-length moves.</summary>
internal sealed class PathWriter
{
    public readonly OpPath Op; public Pt Cur;
    public PathWriter(OpPath op) { Op = op; Cur = op.Start; }
    public void Rapid(Pt p) { if (p.DistanceTo(Cur) > 1e-5) { Op.Moves.Add(new Move(MoveKind.Rapid, p)); Cur = p; } }
    public void Rapid(double z, double r) => Rapid(new Pt(z, r));
    public void Feed(Pt p, double f) { if (p.DistanceTo(Cur) > 1e-5) { Op.Moves.Add(new Move(MoveKind.Feed, p, f)); Cur = p; } }
    public void Feed(double z, double r, double f) => Feed(new Pt(z, r), f);
    public void Thread(Pt p, double pitch) { Op.Moves.Add(new Move(MoveKind.Thread, p, 0, pitch)); Cur = p; }
    /// <summary>Return to the tool-change position: radially out first, then along Z.</summary>
    public void Home() { Rapid(Cur.Z, Math.Max(Cur.R, Op.Start.R)); Rapid(Op.Start); }
}

/// <summary>Shared geometry for one CAM run (all regions in real coordinates).</summary>
public sealed class CamContext
{
    public LatheProject Project { get; }
    public MachineProfile Machine { get; }
    public PartGeometry G { get; }
    public double Tol { get; }
    public Pt ToolChange { get; }
    /// <summary>Solid outside shape + bar remainder wall, full section (OD keep-out source).</summary>
    public PathsD OdObstacle { get; }
    /// <summary>Chuck body: never touch.</summary>
    public PathsD Chuck { get; }
    /// <summary>Finished part upper half (with grooves, bore and drilled holes), used for gouge checks.</summary>
    public PathsD PartUpper { get; private set; } = new();
    /// <summary>Region of the bar that remains after part-off (or after the last feature if not parted).</summary>
    public PathsD BarRemainderUpper { get; private set; } = new();
    public PathsD ThreadZonesUpper { get; } = new();
    public PathsD StockUpper { get; }
    public List<PathsD> DrillHolesUpper { get; } = new();
    public PathsD BoreUpper { get; } = new();
    public double PartOffWidth { get; set; }

    public CamContext(LatheProject project, MachineProfile machine, PartGeometry geo)
    {
        Project = project; Machine = machine; G = geo;
        Tol = Math.Clamp(machine.Tolerance, 0.0005, 0.05);
        double sr = geo.StockR;
        ToolChange = new Pt(geo.StockFaceZ + machine.ToolChangeRightOfFace, sr + machine.ToolChangeAboveStock);
        StockUpper = Geo.One(Geo.Rect(geo.JawZ, geo.StockFaceZ, geo.StockBoreR, sr));
        double chuckR = Math.Max(machine.ChuckDiameter / 2, sr + 5);
        Chuck = Geo.One(Geo.Rect(geo.JawZ - 120, geo.JawZ, -chuckR, chuckR));

        var od = geo.Od.ToPolyline(Tol);
        double L = geo.Length, far = geo.JawZ - 10, keep = sr + 30;
        var upper = new List<Pt>(od) { new(-L, keep), new(far, keep), new(far, 0) };
        OdObstacle = Geo.Union(Geo.Full(Geo.One(Geo.Path(upper))), Chuck);

        if (!geo.Id.IsEmpty)
        {
            var id = geo.Id.ToPolyline(Tol);
            var hole = new List<Pt> { new(5, id[0].R) };
            hole.AddRange(id);
            hole.Add(new Pt(id[^1].Z, 0)); hole.Add(new Pt(5, 0));
            BoreUpper = Geo.One(Geo.Path(hole));
        }
        foreach (var t in geo.Threads)
        {
            var sp = t.Spec;
            ThreadZonesUpper.AddRange(t.T.Internal
                ? Geo.One(Geo.Rect(t.ZLeft - 0.5, t.ZRight + 0.5, sp.InternalMinorDia / 2 - 0.2, sp.MajorDia / 2 + 0.05))
                : Geo.One(Geo.Rect(t.ZLeft - 0.5, t.ZRight + 0.5, sp.ExternalMinorDia / 2 - 0.05, sp.MajorDia / 2 + 0.5)));
        }
    }

    public void AddDrillHole(double dia, double fullDepth, double pointLen, double zTop)
    {
        var p = Geo.Path(new[] { new Pt(zTop + 5, dia / 2), new Pt(-fullDepth, dia / 2), new Pt(-fullDepth - pointLen, 0), new Pt(zTop + 5, 0) });
        DrillHolesUpper.Add(Geo.One(p));
    }

    /// <summary>Call once all drill ops are known: builds the finished-part and bar-remainder regions.</summary>
    public void Complete()
    {
        var od = G.Od.ToPolyline(Tol);
        double L = G.Length;
        var solid = new List<Pt>(od) { new(-L, 0) };
        var part = Geo.One(Geo.Path(solid));
        foreach (var g in G.Grooves) part = Geo.Diff(part, Geo.One(Geo.Rect(g.ZLeft, g.ZRight, g.R, G.StockR + 5)));
        part = Geo.Diff(part, Holes());
        if (G.StockBoreR > 0) part = Geo.Diff(part, Geo.One(Geo.Rect(-L - 1, 5, 0, G.StockBoreR)));
        PartUpper = part;

        double barEnd = G.PartOff ? -L - PartOffWidth : -L;
        var bar = Geo.One(Geo.Rect(G.JawZ, barEnd, G.StockBoreR, G.StockR));
        BarRemainderUpper = Geo.Diff(bar, Holes());
    }

    public PathsD Holes()
    {
        var h = new PathsD(BoreUpper);
        foreach (var d in DrillHolesUpper) h.AddRange(d);
        return h.Count == 0 ? h : Geo.Union(h);
    }

    /// <summary>Obstacle for inside (boring) tools: everything except the bore, drilled holes and the space right of the face.</summary>
    public PathsD IdObstacle(double faceZ)
    {
        double far = G.JawZ - 10, big = G.StockR + 30;
        var solid = Geo.One(Geo.Rect(far, faceZ, -big, big));
        var holes = Holes();
        if (G.StockBoreR > 0) holes = Geo.Union(holes, Geo.One(Geo.Rect(far, faceZ + 5, 0, G.StockBoreR)));
        return Geo.Union(Geo.Diff(solid, Geo.Full(holes)), Chuck);
    }
}
