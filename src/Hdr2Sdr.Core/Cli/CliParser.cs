using System.Globalization;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Core.Cli;

public static class CliParser
{
    public const string Usage = """
        hdr2sdr - HDR to SDR post-capture action for ShareX

        usage: hdr2sdr.exe <input.png> [options]
               hdr2sdr.exe --list-outputs [--verbose]
               hdr2sdr.exe --capture-all --dump-dir <dir> [--verbose]

        Re-captures the desktop in HDR, finds where <input.png> came from, tonemaps that
        region to SDR, overwrites <input.png> and copies the result to the clipboard.

        options:
          --tonemap desktop|hable|aces  tonemapping operator (default desktop)
          --exposure <float>            linear gain before tonemapping (default 1.0)
          --knee <0..1>                 desktop operator: fraction of SDR white where the
                                        BT.2390 roll-off starts; 1.0 = exact SDR, clip above
          --sdr-white <nits>            override the monitor's SDR white level
          --peak <nits>                 override the monitor's peak luminance
          --no-clipboard                do not copy the result to the clipboard
          --output <path>               write here instead of overwriting the input
          --dump-dir <dir>              write raw captures and previews for diagnosis
          --verbose                     print progress to stderr
          --list-outputs                list DXGI outputs and their HDR state, then exit
          --capture-all                 capture every output into --dump-dir, then exit
          --help                        show this text

        exit codes: 0 ok, 2 bad arguments, 3 capture failed, 4 region not found, 5 write/clipboard failed
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        if (args.Length == 0) return o with { Mode = RunMode.Help };

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--help": case "-h": case "/?":
                    return o with { Mode = RunMode.Help };
                case "--list-outputs":
                    o = o with { Mode = RunMode.ListOutputs }; break;
                case "--capture-all":
                    o = o with { Mode = RunMode.CaptureAll }; break;
                case "--no-clipboard":
                    o = o with { NoClipboard = true }; break;
                case "--verbose":
                    o = o with { Verbose = true }; break;
                case "--tonemap":
                {
                    string v = Value(args, ref i);
                    if (!TonemapperFactory.Names.Contains(v.ToLowerInvariant()))
                        throw new CliException($"Unknown tonemapper '{v}'. Choose one of: {string.Join(", ", TonemapperFactory.Names)}.");
                    o = o with { Tonemap = v.ToLowerInvariant() };
                    break;
                }
                case "--exposure":
                {
                    float v = Float(args, ref i);
                    if (v <= 0f) throw new CliException("--exposure must be greater than 0.");
                    o = o with { Exposure = v };
                    break;
                }
                case "--knee":
                {
                    float v = Float(args, ref i);
                    if (v <= 0f || v > 1f) throw new CliException("--knee must be in (0, 1].");
                    o = o with { Knee = v };
                    break;
                }
                case "--sdr-white":
                {
                    float v = Float(args, ref i);
                    if (v <= 0f) throw new CliException("--sdr-white must be greater than 0.");
                    o = o with { SdrWhiteNits = v };
                    break;
                }
                case "--peak":
                {
                    float v = Float(args, ref i);
                    if (v <= 0f) throw new CliException("--peak must be greater than 0.");
                    o = o with { PeakNits = v };
                    break;
                }
                case "--output":
                    o = o with { OutputPath = Value(args, ref i) }; break;
                case "--dump-dir":
                    o = o with { DumpDir = Value(args, ref i) }; break;
                default:
                    if (a.StartsWith('-')) throw new CliException($"Unknown option '{a}'.");
                    if (o.InputPath != null) throw new CliException($"Unexpected extra argument '{a}'; only one input file is accepted.");
                    o = o with { InputPath = a };
                    break;
            }
        }

        if (o.Mode == RunMode.Process && o.InputPath == null) throw new CliException("An input PNG path is required.");
        if (o.Mode == RunMode.CaptureAll && o.DumpDir == null) throw new CliException("--capture-all requires --dump-dir.");
        return o;
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new CliException($"Option '{args[i]}' needs a value.");
        return args[++i];
    }

    private static float Float(string[] args, ref int i)
    {
        string name = args[i];
        string v = Value(args, ref i);
        if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            throw new CliException($"Option '{name}' expects a number, got '{v}'.");
        return f;
    }
}
