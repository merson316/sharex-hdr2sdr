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

        using var mutex = new Mutex(true, @"Local\hdr2sdr-helper", out bool first);
        if (!first) return 0;   // already running

        ApplicationConfiguration.Initialize();
        using var service = new HelperService();
        service.Start();
        if (args.Contains("--overlay")) service.OverlayOverride = true;
        if (args.Contains("--no-overlay")) service.OverlayOverride = false;
        using var tray = new TrayApp(service);
        service.Hotkeys.Install();   // hook runs on its own thread
        service.AttachUi(tray.UiControl);
        Application.Run(tray);
        return 0;
    }

    /// <summary>
    /// `hdr2sdr-helper.exe --capture <Job>`: asks the running helper to start the job with the overlay; if no helper
    /// is running, starts the ShareX job directly (the post-capture action then corrects the file).
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
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShareX", "ShareX.exe");
            if (!File.Exists(exe)) return 1;
            Process.Start(new ProcessStartInfo(exe, "-" + job) { UseShellExecute = false, CreateNoWindow = true });
            return 0;
        }
    }

    /// <summary>Runs the capture loops for two seconds, freezes, takes a snapshot in memory and reports timing. Writes no images.</summary>
    private static int SelfTest()
    {
        using var service = new HelperService();
        service.StartLoops();
        Thread.Sleep(2000);
        foreach (CaptureLoop l in service.Loops) Console.WriteLine($"{l.Output.DeviceName}: status={l.Status} hasFrame={l.HasFrame} latest={l.LatestUtc:HH:mm:ss.fff}");
        var sw = Stopwatch.StartNew();
        foreach (CaptureLoop l in service.Loops) l.BeginRecording(250, 12);
        TimeSpan took = service.Store.Take(service.Loops, includeCursor: true);
        Snapshot? s = service.Store.Current;
        Console.WriteLine(s == null ? "snapshot: none" : $"snapshot: {s.Images.Count} outputs in {took.TotalMilliseconds:F0} ms; " + string.Join("; ", s.Header.Outputs.Select(o => $"{o.DeviceName} {o.Width}x{o.Height} hdr={o.Hdr} cursor={(o.Cursor == null ? "none" : $"{o.Cursor.X},{o.Cursor.Y}")}")));
        // pipe round trip against ourselves
        var pipe = new PipeServer(service.Store, () => service.Loops, service.StatusText, service.Log);
        pipe.Start();
        Thread.Sleep(400);
        Console.WriteLine("ring frames recorded: " + string.Join(", ", service.Loops.Select(l => $"{l.Output.DeviceName}={l.RingCount}")));
        var client = new Hdr2Sdr.Windows.Helper.HelperClient(service.Log.Info);
        sw.Restart();
        var got = client.TryGetSnapshot(TimeSpan.FromSeconds(5));
        Console.WriteLine(got == null ? "pipe get: nothing" : $"pipe get: {got.Images.Count} outputs, {got.Images.Sum(i => i.Data.Length) * 2 / 1024 / 1024} MB of halves in {sw.ElapsedMilliseconds} ms");
        if (got != null)
        {
            var o = got.Header.Outputs[0];
            var crops = client.GetRingCrops(o.DeviceName, 100, 100, 320, 200);
            Console.WriteLine($"ring crops for {o.DeviceName}: {crops.Count} -> offsets {string.Join(",", crops.Select(c => c.OffsetMs))} ms");
        }
        client.Consume();
        Console.WriteLine($"after consume: {(service.Store.Current == null ? "empty" : "still held")}, ring={service.Loops.Sum(l => l.RingCount)}");
        pipe.Dispose();
        return got != null && s != null ? 0 : 1;
    }
}
