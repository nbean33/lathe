using Lathe.Core.Tools;

namespace Lathe.Core.Materials;

public enum CutKind { Rough, Finish, Groove, PartOff, Drill, Thread }

/// <summary>Cutting data for one cut kind: surface speed (m/min), feed (mm/rev) and radial depth (mm).</summary>
public sealed record CutData(double Vc, double FeedPerRev, double DepthOfCut);

/// <summary>Conservative starting values sized for hobby lathes (lighter DOC than industrial tables).</summary>
public sealed record MaterialSpec(string Name, IReadOnlyDictionary<(ToolMaterial, CutKind), CutData> Data)
{
    public CutData Get(ToolMaterial tm, CutKind k) => Data[(tm, k)];
    public override string ToString() => Name;
}

public static class MaterialTable
{
    private static MaterialSpec Make(string name,
        (double vc, double f, double doc) hssRough, (double vc, double f) hssFinish, (double vc, double f) hssPart, double hssDrill,
        (double vc, double f, double doc) cRough, (double vc, double f) cFinish, (double vc, double f) cPart)
    {
        var d = new Dictionary<(ToolMaterial, CutKind), CutData>
        {
            [(ToolMaterial.HSS, CutKind.Rough)] = new(hssRough.vc, hssRough.f, hssRough.doc),
            [(ToolMaterial.HSS, CutKind.Finish)] = new(hssFinish.vc, hssFinish.f, 0.2),
            [(ToolMaterial.HSS, CutKind.Groove)] = new(hssPart.vc * 1.2, hssPart.f * 1.2, 0),
            [(ToolMaterial.HSS, CutKind.PartOff)] = new(hssPart.vc, hssPart.f, 0),
            [(ToolMaterial.HSS, CutKind.Drill)] = new(hssDrill, 0, 0),
            [(ToolMaterial.HSS, CutKind.Thread)] = new(hssFinish.vc * 0.4, 0, 0),
            [(ToolMaterial.Carbide, CutKind.Rough)] = new(cRough.vc, cRough.f, cRough.doc),
            [(ToolMaterial.Carbide, CutKind.Finish)] = new(cFinish.vc, cFinish.f, 0.2),
            [(ToolMaterial.Carbide, CutKind.Groove)] = new(cPart.vc * 1.2, cPart.f * 1.2, 0),
            [(ToolMaterial.Carbide, CutKind.PartOff)] = new(cPart.vc, cPart.f, 0),
            [(ToolMaterial.Carbide, CutKind.Drill)] = new(hssDrill * 2.5, 0, 0),
            [(ToolMaterial.Carbide, CutKind.Thread)] = new(cFinish.vc * 0.35, 0, 0),
        };
        return new MaterialSpec(name, d);
    }

    public static readonly IReadOnlyList<MaterialSpec> All = new[]
    {
        Make("Aluminium (6061)", (90, 0.15, 1.5), (120, 0.06), (60, 0.04), 60, (250, 0.20, 2.0), (300, 0.08), (120, 0.06)),
        Make("Brass (360)",      (60, 0.15, 1.5), (80, 0.06),  (45, 0.04), 50, (200, 0.20, 2.0), (250, 0.08), (100, 0.05)),
        Make("Mild steel (1018)",(30, 0.15, 1.0), (40, 0.05),  (20, 0.03), 25, (120, 0.20, 1.5), (160, 0.08), (70, 0.05)),
        Make("Stainless (304)",  (15, 0.10, 0.8), (20, 0.05),  (12, 0.03), 10, (80, 0.15, 1.2),  (100, 0.07), (50, 0.04)),
        Make("Cast iron",        (20, 0.15, 1.0), (25, 0.06),  (15, 0.04), 20, (90, 0.20, 1.5),  (110, 0.08), (60, 0.05)),
        Make("Plastic (Delrin/nylon)", (100, 0.15, 2.0), (150, 0.08), (80, 0.06), 60, (200, 0.20, 2.5), (250, 0.10), (150, 0.08)),
    };

    public static MaterialSpec Find(string? name) => All.FirstOrDefault(m => m.Name == name) ?? All[0];
}

public static class FeedsSpeeds
{
    /// <summary>RPM for a surface speed at a diameter, clamped to [minRpm, maxRpm].</summary>
    public static int Rpm(double vcMetersPerMin, double diameterMm, int maxRpm, int minRpm = 50)
    {
        if (diameterMm <= 0.1) diameterMm = 0.1;
        double rpm = 1000 * vcMetersPerMin / (Math.PI * diameterMm);
        return (int)Math.Round(Math.Clamp(rpm, minRpm, Math.Max(minRpm, maxRpm)));
    }

    /// <summary>Drill feed per rev grows with diameter: ~0.02 mm/rev per mm, between 0.02 and 0.25.</summary>
    public static double DrillFeed(double dia) => Math.Clamp(0.02 * dia, 0.02, 0.25);

    public static double MmPerMin(double feedPerRev, int rpm) => feedPerRev * rpm;
}
