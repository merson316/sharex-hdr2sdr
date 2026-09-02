using Hdr2Sdr.App.Display;
using Hdr2Sdr.Core.Cli;

namespace Hdr2Sdr.App;

internal static class Pipeline
{
    public static int CaptureAll(CliOptions opts, DisplaySet set, Log log)
    {
        log.Error("--capture-all is not implemented yet");
        return ExitCodes.CaptureFailed;
    }

    public static int Process(CliOptions opts, DisplaySet set, Log log)
    {
        log.Error("processing is not implemented yet");
        return ExitCodes.CaptureFailed;
    }
}
