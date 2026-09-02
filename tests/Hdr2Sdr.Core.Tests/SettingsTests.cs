using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Config;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_are_the_v01_behaviour()
    {
        var s = new Settings();
        Assert.Equal("desktop", s.Tonemap);
        Assert.Equal(1f, s.Exposure);
        Assert.Equal(1f, s.Knee);
        Assert.Null(s.SdrWhiteNits);
        Assert.Null(s.PeakNits);
        Assert.Equal(0.9f, s.JpegQuality);
        Assert.Equal(90, s.WebpQuality);
        Assert.Equal("auto", s.Cursor);
        Assert.Equal("none", s.HdrSidecar);
        Assert.True(s.UseHelper);
    }

    [Fact]
    public void Round_trips_through_json_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdr2sdr-settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new Settings { Tonemap = "hable", Exposure = 1.5f, Knee = 0.6f, SdrWhiteNits = 203f, WebpQuality = 101, Cursor = "on", HdrSidecar = "jxr", UseHelper = false };
            SettingsFile.Save(path, s);
            var (back, error) = SettingsFile.Load(path);
            Assert.Null(error);
            Assert.Equal(s, back);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_gives_defaults_without_error()
    {
        var (s, error) = SettingsFile.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-hdr2sdr.json"));
        Assert.Equal(new Settings(), s);
        Assert.Null(error);
    }

    [Fact]
    public void Malformed_file_gives_defaults_and_an_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdr2sdr-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var (s, error) = SettingsFile.Load(path);
            Assert.Equal(new Settings(), s);
            Assert.NotNull(error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_fields_and_partial_files_are_tolerated()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdr2sdr-partial-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"knee\": 0.5, \"futureOption\": true }");
        try
        {
            var (s, error) = SettingsFile.Load(path);
            Assert.Null(error);
            Assert.Equal(0.5f, s.Knee);
            Assert.Equal("desktop", s.Tonemap);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdr2sdr-range-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"knee\": 7, \"exposure\": -1, \"webpQuality\": 500, \"tonemap\": \"bogus\" }");
        try
        {
            var (s, error) = SettingsFile.Load(path);
            Assert.Equal(1f, s.Knee);
            Assert.Equal(0.01f, s.Exposure);
            Assert.Equal(101, s.WebpQuality);
            Assert.Equal("desktop", s.Tonemap);
            Assert.NotNull(error);   // reports what it had to fix
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cli_flags_override_file_only_where_given()
    {
        var file = new Settings { Tonemap = "hable", Exposure = 2f, Knee = 0.5f, WebpQuality = 80 };
        var cli = CliParser.Parse(new[] { "in.png", "--knee", "0.7", "--sidecar", "jxr", "--no-helper" });
        Settings eff = file.ApplyCli(cli);
        Assert.Equal("hable", eff.Tonemap);      // from file
        Assert.Equal(2f, eff.Exposure);          // from file
        Assert.Equal(0.7f, eff.Knee);            // from CLI
        Assert.Equal("jxr", eff.HdrSidecar);     // from CLI
        Assert.False(eff.UseHelper);             // from CLI
        Assert.Equal(80, eff.WebpQuality);       // from file
    }

    [Fact]
    public void New_cli_flags_parse_and_validate()
    {
        var o = CliParser.Parse(new[] { "in.png", "--webp-quality", "lossless", "--jpeg-quality", "0.8", "--cursor", "off", "--settings", @"C:\x\s.json" });
        Assert.Equal(101, o.WebpQuality);
        Assert.Equal(0.8f, o.JpegQuality);
        Assert.Equal("off", o.Cursor);
        Assert.Equal(@"C:\x\s.json", o.SettingsPath);
        Assert.Null(o.Tonemap);                  // not given => null, so the file value can win
        Assert.Null(o.Exposure);
        Assert.Null(o.Knee);
        Assert.Throws<CliException>(() => CliParser.Parse(new[] { "in.png", "--cursor", "maybe" }));
        Assert.Throws<CliException>(() => CliParser.Parse(new[] { "in.png", "--sidecar", "exr" }));
        Assert.Throws<CliException>(() => CliParser.Parse(new[] { "in.png", "--webp-quality", "200" }));
        Assert.Throws<CliException>(() => CliParser.Parse(new[] { "in.png", "--jpeg-quality", "2" }));
    }
}
