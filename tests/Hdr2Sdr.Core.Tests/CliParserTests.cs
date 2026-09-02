using Hdr2Sdr.Core.Cli;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class CliParserTests
{
    [Fact]
    public void Input_only_uses_defaults()
    {
        var o = CliParser.Parse(new[] { @"C:\shots\a.png" });
        Assert.Equal(RunMode.Process, o.Mode);
        Assert.Equal(@"C:\shots\a.png", o.InputPath);
        Assert.Null(o.Tonemap);      // not given: settings.json or the default applies
        Assert.Null(o.Exposure);
        Assert.Null(o.Knee);
        Assert.Null(o.SdrWhiteNits);
        Assert.Null(o.PeakNits);
        Assert.False(o.NoClipboard);
        Assert.Null(o.OutputPath);
        Assert.Null(o.DumpDir);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void All_flags_parse()
    {
        var o = CliParser.Parse(new[]
        {
            "in.png", "--tonemap", "hable", "--exposure", "1.5", "--knee", "0.6", "--sdr-white", "203",
            "--peak", "800", "--no-clipboard", "--output", "out.png", "--dump-dir", "dbg", "--verbose",
        });
        Assert.Equal("hable", o.Tonemap);
        Assert.Equal(1.5f, o.Exposure);
        Assert.Equal(0.6f, o.Knee);
        Assert.Equal(203f, o.SdrWhiteNits);
        Assert.Equal(800f, o.PeakNits);
        Assert.True(o.NoClipboard);
        Assert.Equal("out.png", o.OutputPath);
        Assert.Equal("dbg", o.DumpDir);
        Assert.True(o.Verbose);
    }

    [Fact]
    public void Diagnostic_modes_do_not_need_input()
    {
        Assert.Equal(RunMode.ListOutputs, CliParser.Parse(new[] { "--list-outputs" }).Mode);
        Assert.Equal(RunMode.CaptureAll, CliParser.Parse(new[] { "--capture-all", "--dump-dir", "d" }).Mode);
        Assert.Equal(RunMode.Help, CliParser.Parse(new[] { "--help" }).Mode);
        Assert.Equal(RunMode.Help, CliParser.Parse(Array.Empty<string>()).Mode);
    }

    [Theory]
    [InlineData("--tonemap")]                        // missing value
    [InlineData("in.png", "--tonemap", "reinhard")]  // unknown tonemapper
    [InlineData("in.png", "--exposure", "abc")]      // not a number
    [InlineData("in.png", "--exposure", "0")]        // must be > 0
    [InlineData("in.png", "--knee", "1.5")]          // must be in (0,1]
    [InlineData("in.png", "--bogus")]                // unknown flag
    [InlineData("a.png", "b.png")]                   // two inputs
    [InlineData("--capture-all")]                    // needs --dump-dir
    public void Bad_arguments_throw(params string[] args)
        => Assert.Throws<CliException>(() => CliParser.Parse(args));

    [Fact]
    public void Usage_mentions_every_flag()
    {
        foreach (string flag in new[] { "--tonemap", "--exposure", "--knee", "--sdr-white", "--peak", "--no-clipboard", "--output", "--dump-dir", "--verbose", "--list-outputs", "--capture-all" })
            Assert.Contains(flag, CliParser.Usage);
    }
}
