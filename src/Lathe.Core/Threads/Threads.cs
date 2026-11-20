namespace Lathe.Core.Threads;

public sealed record ThreadSpec(string Name, double MajorDia, double Pitch, string Family)
{
    /// <summary>Radial depth of an external 60° thread (ISO 68 / UN, with root radius).</summary>
    public double ExternalDepth => 0.61343 * Pitch;
    /// <summary>Radial depth of an internal thread (basic profile, H1).</summary>
    public double InternalDepth => 0.54127 * Pitch;
    public double ExternalMinorDia => MajorDia - 2 * ExternalDepth;
    /// <summary>Internal minor diameter D1 = D - 1.0825 P: the bore size before internal threading.</summary>
    public double InternalMinorDia => MajorDia - 1.08253 * Pitch;
    public double Tpi => 25.4 / Pitch;
    public override string ToString() => Name;
}

public static class ThreadTable
{
    private static ThreadSpec M(double d, double p, bool fine = false) =>
        new($"M{d:0.##}x{p:0.##}", d, p, fine ? "Metric fine" : "Metric coarse");
    private static ThreadSpec U(string name, double dIn, int tpi, string fam) => new($"{name}-{tpi} {fam}", dIn * 25.4, 25.4 / tpi, fam);

    public static readonly IReadOnlyList<ThreadSpec> All = new List<ThreadSpec>
    {
        M(3, 0.5), M(4, 0.7), M(5, 0.8), M(6, 1), M(8, 1.25), M(10, 1.5), M(12, 1.75), M(14, 2), M(16, 2),
        M(18, 2.5), M(20, 2.5), M(22, 2.5), M(24, 3), M(27, 3), M(30, 3.5),
        M(6, 0.75, true), M(8, 1, true), M(10, 1, true), M(10, 1.25, true), M(12, 1.25, true), M(12, 1.5, true),
        M(14, 1.5, true), M(16, 1.5, true), M(18, 1.5, true), M(20, 1.5, true), M(22, 1.5, true), M(24, 2, true),
        U("#4", 0.112, 40, "UNC"), U("#6", 0.138, 32, "UNC"), U("#8", 0.164, 32, "UNC"), U("#10", 0.190, 24, "UNC"),
        U("1/4", 0.25, 20, "UNC"), U("5/16", 0.3125, 18, "UNC"), U("3/8", 0.375, 16, "UNC"), U("7/16", 0.4375, 14, "UNC"),
        U("1/2", 0.5, 13, "UNC"), U("5/8", 0.625, 11, "UNC"), U("3/4", 0.75, 10, "UNC"), U("1", 1.0, 8, "UNC"),
        U("#10", 0.190, 32, "UNF"), U("1/4", 0.25, 28, "UNF"), U("5/16", 0.3125, 24, "UNF"), U("3/8", 0.375, 24, "UNF"),
        U("7/16", 0.4375, 20, "UNF"), U("1/2", 0.5, 20, "UNF"), U("5/8", 0.625, 18, "UNF"), U("3/4", 0.75, 16, "UNF"),
        U("1", 1.0, 12, "UNF"),
    };

    public static ThreadSpec? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolve a thread feature's spec, including "Custom".</summary>
    public static ThreadSpec? Resolve(string name, double customMajor, double customPitch)
    {
        if (string.Equals(name?.Trim(), "Custom", StringComparison.OrdinalIgnoreCase))
            return customMajor > 0 && customPitch > 0 ? new ThreadSpec("Custom", customMajor, customPitch, "Custom") : null;
        return Find(name ?? "");
    }
}

public static class ThreadCalc
{
    /// <summary>
    /// Cumulative radial depth after each pass using constant chip area (depth_k = d1 * sqrt(k)),
    /// never less than minDepth per pass, ending exactly at totalDepth, followed by spring passes at full depth.
    /// </summary>
    public static List<double> PassDepths(double totalDepth, double firstDepth, double minDepth, int springPasses)
    {
        if (totalDepth <= 0) throw new ArgumentOutOfRangeException(nameof(totalDepth));
        firstDepth = Math.Clamp(firstDepth, 0.01, totalDepth);
        minDepth = Math.Clamp(minDepth, 0.005, firstDepth);
        var list = new List<double>();
        double prev = 0;
        for (int k = 1; k < 500; k++)
        {
            double d = firstDepth * Math.Sqrt(k);
            if (d - prev < minDepth) d = prev + minDepth;
            if (d >= totalDepth - 1e-6 || totalDepth - d < minDepth * 0.5) { list.Add(totalDepth); break; }
            list.Add(d); prev = d;
        }
        for (int i = 0; i < Math.Max(0, springPasses); i++) list.Add(totalDepth);
        return list;
    }
}
