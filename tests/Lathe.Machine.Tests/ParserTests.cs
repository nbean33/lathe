using Lathe.Machine;

namespace Lathe.Machine.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesMPosWithWco_ThreeAxes()
    {
        var s = GrblParser.ParseStatus("<Idle|MPos:10.000,0.000,-5.000|FS:0,0|WCO:2.000,0.000,-1.000>", null)!;
        Assert.Equal(MachineState.Idle, s.State);
        Assert.Equal(10, s.MX, 6);
        Assert.Equal(-5, s.MZ, 6);
        Assert.Equal(8, s.WX, 6);
        Assert.Equal(-4, s.WZ, 6);
        Assert.True(s.HasWco);
    }

    [Fact]
    public void ParsesWPosUsingLastWco_TwoAxes()
    {
        var s = GrblParser.ParseStatus("<Run|WPos:8.000,-4.000|FS:120,800>", (2, -1))!;
        Assert.Equal(MachineState.Run, s.State);
        Assert.Equal(10, s.MX, 6);
        Assert.Equal(-5, s.MZ, 6);
        Assert.Equal(120, s.Feed);
        Assert.Equal(800, s.SpindleRpm);
        Assert.False(s.HasWco);
    }

    [Fact]
    public void ParsesSubstateOverridesPinsAccessories()
    {
        var s = GrblParser.ParseStatus("<Hold:1|MPos:0,0,0|FS:0,500|Ov:110,50,90|Pn:XZ|A:SF>", null)!;
        Assert.Equal(MachineState.Hold, s.State);
        Assert.Equal(1, s.SubState);
        Assert.Equal((110, 50, 90), (s.FeedOv, s.RapidOv, s.SpindleOv));
        Assert.Equal("XZ", s.Pins);
        Assert.Equal("SF", s.Accessories);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("<garbage")]
    [InlineData("<Idle|FS:0,0>")]
    public void RejectsNonStatus(string line) => Assert.Null(GrblParser.ParseStatus(line, null));

    [Fact]
    public void ParsesSettings()
    {
        Assert.True(GrblParser.TryParseSetting("$32=2", out var id, out var v));
        Assert.Equal(32, id);
        Assert.Equal("2", v);
        Assert.True(GrblParser.TryParseSetting("$130=150.000", out id, out v));
        Assert.Equal((130, "150.000"), (id, v));
        Assert.False(GrblParser.TryParseSetting("ok", out _, out _));
        Assert.False(GrblParser.TryParseSetting("$N0=", out _, out _));
    }

    [Fact]
    public void CodeTables()
    {
        Assert.Contains("Unsupported", GrblCodes.Error(20));
        Assert.Contains("Soft limit", GrblCodes.Alarm(2));
        Assert.Contains("999", GrblCodes.Error(999));
        Assert.Contains("999", GrblCodes.Alarm(999));
    }

    [Theory]
    [InlineData("G0 X10 (move) Z5 ; rapid", "G0X10Z5")]
    [InlineData("  g1 x1.5 f0.1  ", "G1X1.5F0.1")]
    [InlineData("%", "")]
    [InlineData("(only a comment)", "")]
    public void CleansLines(string raw, string expected) => Assert.Equal(expected, Streamer.Clean(raw));
}
