using Lathe.Core;
using Lathe.Core.Geometry;
using Lathe.Core.Materials;
using Lathe.Core.Model;
using Lathe.Core.Project;
using Lathe.Core.Threads;

namespace Lathe.Tests;

public class CoreTests
{
    [Fact]
    public void M10x1_5_external_depth_and_minor()
    {
        var t = ThreadTable.Find("M10x1.5")!;
        Assert.Equal(0.920, t.ExternalDepth, 3);
        Assert.Equal(8.160, t.ExternalMinorDia, 2);
        Assert.Equal(8.376, t.InternalMinorDia, 2);
    }

    [Fact]
    public void Half_inch_13_UNC()
    {
        var t = ThreadTable.Find("1/2-13 UNC")!;
        Assert.Equal(12.7, t.MajorDia, 3);
        Assert.Equal(1.9538, t.Pitch, 3);
        Assert.InRange(t.ExternalDepth, 1.197, 1.200);
    }

    [Fact]
    public void Thread_passes_are_increasing_end_at_depth_and_have_spring_passes()
    {
        var p = ThreadCalc.PassDepths(0.92, 0.3, 0.03, 2);
        Assert.Equal(0.3, p[0], 6);
        Assert.Equal(0.92, p[^1], 6);
        Assert.Equal(0.92, p[^2], 6);
        Assert.Equal(0.92, p[^3], 6);
        for (int i = 1; i < p.Count - 3; i++) Assert.True(p[i] - p[i - 1] >= 0.03 - 1e-9);
    }

    [Fact]
    public void Rpm_is_capped()
    {
        Assert.Equal(2500, FeedsSpeeds.Rpm(250, 10, 2500));
        Assert.Equal(1273, FeedsSpeeds.Rpm(40, 10, 5000));
        Assert.Equal(50, FeedsSpeeds.Rpm(1, 500, 3000));
    }

    [Fact]
    public void Arc_offset_radius_matches()
    {
        var a = new ArcSeg(new Pt(0, 0), new Pt(-5, 5), new Pt(-5, 0), Ccw: true);
        Assert.Equal(5, a.Radius, 9);
        Assert.Equal(Math.PI / 2 * 5, a.Length, 6);
        foreach (var p in a.Sample(0.001)) Assert.Equal(5, p.DistanceTo(a.C), 6);
    }

    [Fact]
    public void Chamfer_and_fillet_break_corners()
    {
        var part = new PartModel();
        part.OdFeatures.Add(new CylinderFeature { Diameter = 20, Length = 10, StartEdge = EdgeBreak.Ch(1), EndEdge = EdgeBreak.Rad(1) });
        part.OdFeatures.Add(new CylinderFeature { Diameter = 24, Length = 10 });
        var g = PartBuilder.Build(new Stock { Diameter = 25, StickOut = 40 }, part);
        Assert.True(g.Ok, string.Join("\n", g.Issues));
        Assert.Contains(g.Od.Segments, s => s is LineSeg l && Math.Abs(l.A.R - 9) < 1e-9 && Math.Abs(l.B.Z + 1) < 1e-9);
        Assert.Contains(g.Od.Segments, s => s is ArcSeg a && Math.Abs(a.Radius - 1) < 1e-9);
        Assert.Equal(20, g.Length, 9);
    }

    [Fact]
    public void Validation_catches_common_mistakes()
    {
        var part = new PartModel();
        part.OdFeatures.Add(new CylinderFeature { Diameter = 30, Length = 10 });
        part.OdFeatures.Add(new CylinderFeature { Diameter = 10, Length = 0 });
        part.Grooves.Add(new Groove { Position = 2, Width = 2, Diameter = 40 });
        var g = PartBuilder.Build(new Stock { Diameter = 25, StickOut = 40 }, part);
        Assert.Contains(g.Issues, i => i.Message.Contains("larger than the stock"));
        Assert.Contains(g.Issues, i => i.Message.Contains("Length must be greater"));
        Assert.Contains(g.Issues, i => i.Message.Contains("Groove must be smaller"));
    }

    [Fact]
    public void Project_round_trips_through_json_atomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lathe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "p.lathe");
            var p = Templates.Stud();
            Store.SaveProject(path, p);
            Store.SaveProject(path, p); // second save makes a .bak
            Assert.True(File.Exists(path + ".bak"));
            var q = Store.LoadProject(path);
            Assert.Equal(p.Part.OdFeatures.Count, q.Part.OdFeatures.Count);
            Assert.IsType<CylinderFeature>(q.Part.OdFeatures[0]);
            Assert.Equal(p.Operations.Count, q.Operations.Count);
            Assert.Equal("M10x1.5", q.Part.Threads[0].Spec);
            Assert.Equal(1.0, ((CylinderFeature)q.Part.OdFeatures[0]).StartEdge.Size);

            File.WriteAllText(path, "{ not json");
            Assert.Throws<InvalidDataException>(() => Store.LoadProject(path));
            File.WriteAllText(path, "{\"SchemaVersion\": 99}");
            var ex = Assert.Throws<InvalidDataException>(() => Store.LoadProject(path));
            Assert.Contains("newer version", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Corrupt_machine_profile_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "lathe-m-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "garbage");
        string? warn = null;
        var m = Store.LoadOrDefault(path, () => new MachineProfile(), w => warn = w);
        Assert.Equal(2500, m.MaxRpm);
        Assert.NotNull(warn);
        File.Delete(path); File.Delete(path + ".corrupt");
    }
}
