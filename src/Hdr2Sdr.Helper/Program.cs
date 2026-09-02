using System.Diagnostics;
using System.Windows.Forms;

namespace Hdr2Sdr.Helper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();

        using var mutex = new Mutex(true, @"Local\hdr2sdr-helper", out bool first);
        if (!first) return 0;   // already running

        ApplicationConfiguration.Initialize();
        using var service = new HelperService();
        service.Start();
        using var tray = new TrayApp(service);
        service.Hotkeys.Install();   // UI thread pumps messages for the hook
        Application.Run(tray);
        return 0;
    }

    /// <summary>Runs the capture loops for two seconds, freezes, takes a snapshot in memory and reports timing. Writes no images.</summary>
    private static int SelfTest()
    {
        using var service = new HelperService();
        service.StartLoops();
        Thread.Sleep(2000);
        foreach (CaptureLoop l in service.Loops) Console.WriteLine($"{l.Output.DeviceName}: status={l.Status} hasFrame={l.HasFrame} latest={l.LatestUtc:HH:mm:ss.fff}");
        var sw = Stopwatch.StartNew();
        TimeSpan took = service.Store.Take(service.Loops, includeCursor: true);
        Snapshot? s = service.Store.Current;
        Console.WriteLine(s == null ? "snapshot: none" : $"snapshot: {s.Images.Count} outputs in {took.TotalMilliseconds:F0} ms; " + string.Join("; ", s.Header.Outputs.Select(o => $"{o.DeviceName} {o.Width}x{o.Height} hdr={o.Hdr} cursor={(o.Cursor == null ? "none" : $"{o.Cursor.X},{o.Cursor.Y}")}")));
        // pipe round trip against ourselves
        var pipe = new PipeServer(service.Store, service.StatusText, service.Log);
        pipe.Start();
        Thread.Sleep(200);
        var client = new Hdr2Sdr.Windows.Helper.HelperClient(service.Log.Info);
        sw.Restart();
        var got = client.TryGetSnapshot(TimeSpan.FromSeconds(5));
        Console.WriteLine(got == null ? "pipe get: nothing" : $"pipe get: {got.Images.Count} outputs, {got.Images.Sum(i => i.Data.Length) * 2 / 1024 / 1024} MB of halves in {sw.ElapsedMilliseconds} ms");
        client.Consume();
        Console.WriteLine($"after consume: {(service.Store.Current == null ? "empty" : "still held")}");
        pipe.Dispose();
        return got != null && s != null ? 0 : 1;
    }
}
