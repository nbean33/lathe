using Lathe.Core.Model;
using Lathe.Core.Tools;

namespace Lathe.Core.Project;

/// <summary>Ready-made starter parts. Each one is valid and generates clean G-code.</summary>
public static class Templates
{
    public static IReadOnlyList<(string Name, Func<LatheProject> Make)> All { get; } = new List<(string, Func<LatheProject>)>
    {
        ("Shaft with shoulder", Shaft),
        ("Bushing (drilled and bored)", Bushing),
        ("Threaded stud M10", Stud),
        ("Pin with ball end and cove", Pin),
    };

    public static LatheProject New()
    {
        var p = new LatheProject { Name = "New part" };
        p.Stock.Diameter = 25; p.Stock.StickOut = 50; p.Stock.FaceAllowance = 1;
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 20, Length = 20, StartEdge = EdgeBreak.Ch(0.5) });
        p.Tools.Add(ToolDef.Turn(1)); p.Tools.Add(ToolDef.Parting(2));
        StandardOps(p, partTool: 2);
        return p;
    }

    public static LatheProject Shaft()
    {
        var p = new LatheProject { Name = "Shaft with shoulder" };
        p.Stock.Diameter = 25; p.Stock.StickOut = 60;
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 16, Length = 20, StartEdge = EdgeBreak.Ch(0.5) });
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 22, Length = 15, StartEdge = EdgeBreak.Ch(0.5) });
        p.Tools.Add(ToolDef.Turn(1)); p.Tools.Add(ToolDef.Parting(2));
        StandardOps(p, partTool: 2);
        return p;
    }

    public static LatheProject Bushing()
    {
        var p = new LatheProject { Name = "Bushing" };
        p.Stock.Diameter = 30; p.Stock.StickOut = 50;
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 28, Length = 25, StartEdge = EdgeBreak.Ch(0.5) });
        p.Part.IdFeatures.Add(new CylinderFeature { Diameter = 16, Length = 30, StartEdge = EdgeBreak.Ch(0.5) });
        p.Tools.Add(ToolDef.Turn(1)); p.Tools.Add(ToolDef.Parting(2)); p.Tools.Add(ToolDef.Center(3));
        p.Tools.Add(ToolDef.DrillOf(4, 12)); p.Tools.Add(ToolDef.Bore(5, 8, 10));
        p.Operations.Add(new FaceOp { ToolNumber = 1 });
        p.Operations.Add(new DrillOp { ToolNumber = 3, Depth = 2, PeckDepth = 0 });
        p.Operations.Add(new DrillOp { ToolNumber = 4, Depth = 30, PeckDepth = 4 });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Rough });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Finish });
        p.Operations.Add(new TurnOp { ToolNumber = 5, Side = TurnSide.Inside, Stage = TurnStage.Rough, DepthOfCut = 0.5 });
        p.Operations.Add(new TurnOp { ToolNumber = 5, Side = TurnSide.Inside, Stage = TurnStage.Finish });
        p.Operations.Add(new PartOffOp { ToolNumber = 2 });
        return p;
    }

    public static LatheProject Stud()
    {
        var p = new LatheProject { Name = "Threaded stud M10" };
        p.Stock.Diameter = 16; p.Stock.StickOut = 50;
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 10, Length = 20, StartEdge = EdgeBreak.Ch(1.0) });
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 14, Length = 10, StartEdge = EdgeBreak.Ch(0.5) });
        p.Part.Grooves.Add(new Groove { Position = 18, Width = 2, Diameter = 8 });
        p.Part.Threads.Add(new ThreadFeature { Spec = "M10x1.5", Position = 0, Length = 18 });
        p.Tools.Add(ToolDef.Turn(1)); p.Tools.Add(ToolDef.Parting(2)); p.Tools.Add(ToolDef.Groove(3, 2)); p.Tools.Add(ToolDef.ThreadOd(4));
        p.Operations.Add(new FaceOp { ToolNumber = 1 });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Rough });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Finish });
        p.Operations.Add(new GrooveOp { ToolNumber = 3 });
        p.Operations.Add(new ThreadOp { ToolNumber = 4 });
        p.Operations.Add(new PartOffOp { ToolNumber = 2 });
        return p;
    }

    public static LatheProject Pin()
    {
        var p = new LatheProject { Name = "Pin with ball end" };
        p.Stock.Diameter = 12; p.Stock.StickOut = 45;
        p.Part.OdFeatures.Add(new ArcFeature { EndDiameter = 10, Length = 5, Radius = 5, Convex = true });
        p.Part.OdFeatures.Add(new ArcFeature { EndDiameter = 10, Length = 6, Radius = 4, Convex = false });
        p.Part.OdFeatures.Add(new CylinderFeature { Diameter = 10, Length = 15 });
        p.Tools.Add(ToolDef.Profile(1)); p.Tools.Add(ToolDef.Parting(2));
        StandardOps(p, partTool: 2);
        return p;
    }

    private static void StandardOps(LatheProject p, int partTool)
    {
        p.Operations.Add(new FaceOp { ToolNumber = 1 });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Rough });
        p.Operations.Add(new TurnOp { ToolNumber = 1, Side = TurnSide.Outside, Stage = TurnStage.Finish });
        p.Operations.Add(new PartOffOp { ToolNumber = partTool });
    }
}
