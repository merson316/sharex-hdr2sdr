using Hdr2Sdr.App.Display;
using Hdr2Sdr.Core.Cli;

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

    private static int Run(CliOptions opts, Log log)
    {
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
                return Pipeline.Process(opts, set, log);
        }
    }
}
