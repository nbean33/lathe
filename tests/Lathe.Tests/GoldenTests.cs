using System.Runtime.CompilerServices;
using Lathe.Cam;
using Lathe.Core.Project;

namespace Lathe.Tests;

/// <summary>
/// The G-code for each template is compared with a committed snapshot, so any change to toolpaths or the
/// post is noticed. To accept intentional changes run: set UPDATE_GOLDEN=1 and dotnet test.
/// </summary>
public class GoldenTests
{
    public static IEnumerable<object[]> Names => Templates.All.Select(t => new object[] { t.Name });

    private static string Dir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden");

    [Theory]
    [MemberData(nameof(Names))]
    public void Gcode_matches_snapshot(string name)
    {
        var p = Templates.All.First(t => t.Name == name).Make();
        var m = new MachineProfile();
        var cam = CamEngine.Generate(p, m);
        var text = GrblHalPost.Build(cam, p, m).Text.Replace("\r\n", "\n");
        var file = Path.Combine(Dir(), string.Concat(name.Where(char.IsLetterOrDigit)) + ".nc");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(file))
        {
            File.WriteAllText(file, text);
            if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") != "1") Assert.Fail($"Snapshot created: {file}. Review it and re-run.");
            return;
        }
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), text);
    }
}
