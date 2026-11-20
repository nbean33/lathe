using Lathe.Cam;
using Lathe.Core;
using Lathe.Core.Model;
using Lathe.Core.Project;
using Lathe.Core.Tools;
using Xunit.Abstractions;

namespace Lathe.Tests;

public class CamBehaviourTests(ITestOutputHelper output)
{
    private static readonly MachineProfile M = new();

    [Fact]
    public void Dump_shaft_gcode()
    {
        var p = Templates.Shaft();
        var cam = CamEngine.Generate(p, M);
        var post = GrblHalPost.Build(cam, p, M);
        foreach (var l in post.Lines) output.WriteLine(l);
    }

    [Fact]
    public void Cove_with_80_degree_insert_leaves_material_instead_of_gouging()
    {
        var p = Templates.Pin();
        p.Tools[0] = ToolDef.Turn(1); // C80 can't descend into the cove
        var cam = CamEngine.Generate(p, M);
        foreach (var i in cam.AllIssues) output.WriteLine(i.ToString());
        Assert.DoesNotContain(cam.AllIssues, i => i.Message.StartsWith("Gouge"));
        Assert.Contains(cam.AllIssues, i => i.Severity == Severity.Warning && i.Message.Contains("Material left"));
    }

    [Fact]
    public void Groove_tool_wider_than_groove_is_an_error()
    {
        var p = Templates.Stud();
        p.Tools.First(t => t.Number == 3).Width = 3;
        var cam = CamEngine.Generate(p, M);
        Assert.False(cam.Ok);
        Assert.Contains(cam.AllIssues, i => i.Message.Contains("wider than the groove"));
    }

    [Fact]
    public void Threading_without_spindle_sync_is_blocked()
    {
        var p = Templates.Stud();
        var cam = CamEngine.Generate(p, M, new CamOptions { SpindleSync = false });
        Assert.False(cam.Ok);
        Assert.Contains(cam.AllIssues, i => i.Message.Contains("spindle sync"));
    }

    [Fact]
    public void Part_too_long_for_stickout_is_rejected()
    {
        var p = Templates.Shaft();
        p.Stock.StickOut = 30;
        var cam = CamEngine.Generate(p, M);
        Assert.False(cam.Ok);
        Assert.Contains(cam.AllIssues, i => i.Message.Contains("stick-out"));
    }

    [Fact]
    public void Simulator_detects_a_rapid_through_material()
    {
        var p = Templates.Shaft();
        var cam = CamEngine.Generate(p, M);
        var op = cam.Ops[1];
        // Inject a rapid straight through the bar.
        op.Moves.Insert(0, new Move(MoveKind.Rapid, new Lathe.Core.Geometry.Pt(-20, 5)));
        var sim = StockSim.Run(cam.Context!, cam.Ops);
        Assert.Contains(sim.Issues, i => i.Message.Contains("Rapid move"));
    }

    [Fact]
    public void Simulator_detects_a_gouge()
    {
        var p = Templates.Shaft();
        var cam = CamEngine.Generate(p, M);
        var op = cam.Ops[2];
        op.Moves.Insert(op.Moves.Count - 2, new Move(MoveKind.Feed, new Lathe.Core.Geometry.Pt(-10, 6), 0.1));
        var sim = StockSim.Run(cam.Context!, cam.Ops);
        Assert.Contains(sim.Issues, i => i.Message.StartsWith("Gouge") || i.Message.Contains("Rapid"));
    }

    [Fact]
    public void Finished_shaft_matches_drawing_within_tolerance()
    {
        var p = Templates.Shaft();
        var cam = CamEngine.Generate(p, M);
        Assert.True(cam.Ok);
        Assert.True(Geo.Area(cam.Sim!.Gouges) < 1e-3);
        Assert.True(Geo.Area(cam.Sim!.Uncut) < 0.05);
    }

    [Fact]
    public void Inch_post_outputs_G20_and_inches()
    {
        var p = Templates.Shaft(); p.Units = UnitSystem.Inch;
        var cam = CamEngine.Generate(p, M);
        var post = GrblHalPost.Build(cam, p, M);
        Assert.Contains(post.Lines, l => l.Contains("G20"));
        Assert.DoesNotContain(post.Lines, l => l.StartsWith("G0 X") && l.Contains("X65"));
    }
}
