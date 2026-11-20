using Lathe.Cam;
using Lathe.Core.Project;
using Lathe.Machine;
using Xunit.Abstractions;

namespace Lathe.Tests;

/// <summary>Template → CAM → simulation → post → stream to the virtual grblHAL controller.</summary>
public class EndToEndTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Names => Templates.All.Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Template_streams_cleanly_to_virtual_controller(string name)
    {
        var project = Templates.All.First(t => t.Name == name).Make();
        var machine = new MachineProfile();
        var cam = CamEngine.Generate(project, machine);
        Assert.True(cam.Ok, string.Join("\n", cam.AllIssues));
        var post = GrblHalPost.Build(cam, project, machine);

        using var sim = new VirtualGrblHal(new VirtualMachineOptions { TimeScale = double.PositiveInfinity, HomingRequired = true });
        using var ctl = new GrblController(sim);
        var rep = await ctl.ConnectAsync();
        Assert.True(rep.Ok, rep.Error);
        Assert.True((await ctl.HomeAsync()).ok);
        // Work zero: centerline at machine X -150 (diameter), finished face at machine Z -200.
        Assert.True((await ctl.SendCommandAsync("G10 L2 P1 X-150 Z-200")).ok);

        var pre = Preflight.Check(cam, post, machine, new MachineFacts(true, true, ctl.IsHomed, rep.Info!.IsLatheMode, rep.Info.HasSpindleSync,
            true, -150, -200, 150, 400, WorkZeroConfirmed: true));
        foreach (var i in pre) output.WriteLine("preflight: " + i);
        Assert.DoesNotContain(pre, i => i.Severity == Lathe.Core.Severity.Error);

        // Controller-side syntax check ($C) first, like the app does.
        var check = ctl.CreateStreamer();
        string? checkAbort = null; check.Aborted += r => checkAbort = r;
        await check.CheckAsync(post.Lines);
        Assert.True(checkAbort is null && check.Errors.Count == 0, checkAbort ?? string.Join("\n", check.Errors));

        var tools = new List<int>();
        var s = ctl.CreateStreamer();
        s.ToolChangeHandler = (t, ct) => { tools.Add(t); return Task.FromResult<IReadOnlyList<string>?>(Array.Empty<string>()); };
        string? aborted = null; bool done = false;
        s.Aborted += r => aborted = r; s.Completed += () => done = true;
        await s.RunAsync(post.Lines, 0, new[] { post.Preamble });
        Assert.True(done, aborted);
        Assert.False(sim.SpindleOn);
        Assert.Equal(1, sim.ProgramEndCount);
        Assert.Equal(cam.Ops.Count, tools.Count);
        // Back at the tool-change position.
        var tc = cam.Context!.ToolChange;
        Assert.Equal(2 * tc.R - 150, 2 * sim.MX, 2); // sim stores radius
        Assert.Equal(tc.Z - 200, sim.MZ, 2);
        output.WriteLine($"{name}: {post.Lines.Count} lines streamed, tools {string.Join(",", tools)}");
    }
}
