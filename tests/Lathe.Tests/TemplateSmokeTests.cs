using Lathe.Cam;
using Lathe.Core;
using Lathe.Core.Project;
using Xunit.Abstractions;

namespace Lathe.Tests;

public class TemplateSmokeTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Names => Templates.All.Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Template_generates_without_errors(string name)
    {
        var project = Templates.All.First(t => t.Name == name).Make();
        var machine = new MachineProfile();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cam = CamEngine.Generate(project, machine);
        var post = GrblHalPost.Build(cam, project, machine);
        output.WriteLine($"{name}: {cam.Ops.Count} ops, {post.Lines.Count} lines, {sw.ElapsedMilliseconds} ms, est {cam.TotalSeconds / 60:0.0} min");
        foreach (var i in cam.AllIssues) output.WriteLine("  " + i);
        foreach (var o in cam.Ops) output.WriteLine($"  {o.Name}: {o.Moves.Count} moves, {o.Rpm} rpm");
        Assert.True(cam.Ok, string.Join("\n", cam.AllIssues.Where(i => i.Severity == Severity.Error)));
        Assert.NotNull(cam.Sim);
    }
}
