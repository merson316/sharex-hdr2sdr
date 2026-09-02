using System.Diagnostics;
using System.Windows.Forms;

namespace Hdr2Sdr.Helper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        int captureIndex = Array.IndexOf(args, "--capture");
        if (captureIndex >= 0) return CaptureCommand(captureIndex + 1 < args.Length ? args[captureIndex + 1] : "RectangleRegion");

        using var mutex = new Mutex(true, @"Local\hdr2sdr", out bool first);
        if (!first) return 0;   // already running

        ApplicationConfiguration.Initialize();
        using var service = new HelperService();
        service.Start();
        using var tray = new TrayApp(service);
        service.Hotkeys.Install();   // hook runs on its own thread
        service.AttachUi(tray.UiControl);
        Application.Run(tray);
        return 0;
    }

    /// <summary>
    /// `hdr2sdr.exe --capture <Job>`: asks the running instance to start the job with the overlay; if none is
    /// running, starts the ShareX job directly.
    /// </summary>
    private static int CaptureCommand(string job)
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeServer.PipeName, System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(500);
            byte[] req = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { op = "capture", job }) + "\n");
            pipe.Write(req); pipe.Flush();
            var buf = new byte[256];
            pipe.Read(buf, 0, buf.Length);
            return 0;
        }
        catch
        {
            return OverlayController.StartShareXJob(job, new HelperLog()) ? 0 : 1;
        }
    }

    /// <summary>Runs the capture loops for two seconds, freezes, snapshots each output in memory and reports timing. Writes no images.</summary>
    private static int SelfTest()
    {
        using var service = new HelperService();
        service.StartLoops();
        Thread.Sleep(2000);
        int ok = 0;
        foreach (CaptureLoop l in service.Loops)
        {
            l.Freeze();
            var sw = Stopwatch.StartNew();
            var frame = l.Snapshot();
            l.Unfreeze();
            Console.WriteLine($"{l.Output.DeviceName}: status={l.Status} hasFrame={l.HasFrame} hdr={l.Output.Hdr} snapshot={(frame == null ? "none" : $"{frame.Width}x{frame.Height} in {sw.ElapsedMilliseconds} ms")}");
            if (frame != null) ok++;
        }
        Console.WriteLine($"hotkeys: {Core.ShareX.ShareXHotkeys.Parse(File.Exists(ShareXPaths.HotkeysConfig) ? File.ReadAllText(ShareXPaths.HotkeysConfig) : "{}").Count} ShareX capture hotkeys found");
        return ok > 0 ? 0 : 1;
    }
}
