using Hdr2Sdr.Windows.Display;
using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Config;

namespace Hdr2Sdr.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        CliOptions opts;
        try
        {
            opts = CliParser.Parse(args);
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(CliParser.Usage);
            return ExitCodes.BadArguments;
        }
        if (opts.Mode == RunMode.Help)
        {
            Console.WriteLine(CliParser.Usage);
            return ExitCodes.Ok;
        }

        using var log = new Log(opts.Verbose);
        try
        {
            return Run(opts, log);
        }
        catch (Exception e)
        {
            log.Error($"unhandled: {e}");
            return ExitCodes.CaptureFailed;
        }
    }

    public static string DefaultSettingsPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hdr2sdr", "settings.json");

    private static int Run(CliOptions opts, Log log)
    {
        string settingsPath = opts.SettingsPath ?? DefaultSettingsPath;
        var (fileSettings, settingsError) = SettingsFile.Load(settingsPath);
        if (settingsError != null) log.Warn($"settings: {settingsError}");
        Settings settings = fileSettings.ApplyCli(opts);
        log.Info($"settings ({(File.Exists(settingsPath) ? settingsPath : "defaults")}): tonemap={settings.Tonemap} exposure={settings.Exposure} knee={settings.Knee} sdrWhite={settings.SdrWhiteNits?.ToString() ?? "auto"} peak={settings.PeakNits?.ToString() ?? "auto"} jpeg={settings.JpegQuality} webp={settings.WebpQuality} cursor={settings.Cursor} sidecar={settings.HdrSidecar} helper={settings.UseHelper}");

        Dictionary<string, DisplayInfo> displays;
        try
        {
            displays = DisplayConfigInterop.Query();
            foreach (DisplayInfo d in displays.Values)
                log.Info($"displayconfig {d.GdiDeviceName}: sdrWhite={d.SdrWhiteNits:F0}nits advancedColor={d.AdvancedColorEnabled}");
        }
        catch (Exception e)
        {
            log.Warn($"DisplayConfig query failed, assuming 80 nits SDR white: {e.Message}");
            displays = new Dictionary<string, DisplayInfo>();
        }

        using DisplaySet set = OutputEnumerator.Enumerate(displays, log.Info);
        foreach (OutputHandle o in set.Outputs) log.Info("output " + o);

        switch (opts.Mode)
        {
            case RunMode.ListOutputs:
                foreach (OutputHandle o in set.Outputs) Console.WriteLine(o);
                return ExitCodes.Ok;
            case RunMode.CaptureAll:
                return Pipeline.CaptureAll(opts, set, log);
            default:
                return Pipeline.Process(opts, settings, set, log);
        }
    }
}
