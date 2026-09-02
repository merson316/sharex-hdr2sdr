using Hdr2Sdr.Core.Config;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_are_exact_sdr_with_monitor_values()
    {
        var s = new Settings();
        Assert.Equal("desktop", s.Tonemap);
        Assert.Equal(1f, s.Exposure);
        Assert.Equal(1f, s.Knee);
        Assert.Null(s.SdrWhiteNits);
        Assert.Null(s.PeakNits);
    }

    [Fact]
    public void Round_trips_through_json_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdr2sdr-settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new Settings { Tonemap = "hable", Exposure = 1.5f, Knee = 0.6f, SdrWhiteNits = 203f };
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
        File.WriteAllText(path, "{ \"knee\": 0.5, \"futureOption\": true, \"useHelper\": false }");
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
        File.WriteAllText(path, "{ \"knee\": 7, \"exposure\": -1, \"tonemap\": \"bogus\" }");
        try
        {
            var (s, error) = SettingsFile.Load(path);
            Assert.Equal(1f, s.Knee);
            Assert.Equal(0.01f, s.Exposure);
            Assert.Equal("desktop", s.Tonemap);
            Assert.NotNull(error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Monitor_values_fill_in_when_not_overridden()
    {
        var p = new Settings { SdrWhiteNits = 250f }.ToTonemapParams(212f, 456f);
        Assert.Equal(250f, p.SdrWhiteNits);
        Assert.Equal(456f, p.PeakNits);
    }
}
