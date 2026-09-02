using Hdr2Sdr.Windows.Display;

namespace Hdr2Sdr.Helper;

/// <summary>Owns the capture loops, hotkey watcher, snapshot store and pipe server.</summary>
public sealed class HelperService : IDisposable
{
    public HelperLog Log { get; } = new();
    public SnapshotStore Store { get; }
    public HotkeyWatcher Hotkeys { get; }
    public List<CaptureLoop> Loops { get; } = new();
    private DisplaySet? _displays;
    private PipeServer? _pipe;
    public bool Paused { get => Hotkeys.Paused; set { Hotkeys.Paused = value; Log.Info(value ? "paused" : "resumed"); } }

    public HelperService()
    {
        Store = new SnapshotStore(Log);
        Hotkeys = new HotkeyWatcher(ShareXPaths.HotkeysConfig, Log);
        Hotkeys.Hotkey += OnHotkey;
    }

    /// <summary>Starts loops and the pipe server. Call Hotkeys.Install() from the UI thread separately.</summary>
    public void Start()
    {
        StartLoops();
        _pipe = new PipeServer(Store, StatusText, Log);
        _pipe.Start();
        Log.Info("helper started");
    }

    public void StartLoops()
    {
        StopLoops();
        Dictionary<string, DisplayInfo> displays;
        try { displays = DisplayConfigInterop.Query(); } catch { displays = new(); }
        _displays = OutputEnumerator.Enumerate(displays, Log.Info);
        foreach (OutputHandle o in _displays.Outputs)
        {
            var loop = new CaptureLoop(o, Log);
            Loops.Add(loop);
            loop.Start();
            Log.Info("loop " + o);
        }
    }

    private void StopLoops()
    {
        foreach (CaptureLoop l in Loops) l.Dispose();
        Loops.Clear();
        _displays?.Dispose();
        _displays = null;
    }

    /// <summary>Re-enumerate outputs after a display change (WM_DISPLAYCHANGE).</summary>
    public void OnDisplayChange()
    {
        Log.Info("display change: restarting capture loops");
        Task.Run(StartLoops);
    }

    private void OnHotkey(Core.Snapshot.HotkeyCombo combo)
    {
        // Runs on the hook thread: freeze immediately, then snapshot on the pool so the hook returns at once.
        foreach (CaptureLoop l in Loops) l.Frozen = true;
        Task.Run(() =>
        {
            try
            {
                bool cursor = CursorPolicy.IncludeCursor(ShareXPaths.SettingsPath, ShareXPaths.ApplicationConfig);
                Store.Take(Loops, cursor);
                Log.Info($"hotkey {combo.Job}: snapshot taken (cursor={cursor})");
            }
            catch (Exception e)
            {
                Log.Error($"snapshot failed: {e.Message}");
                foreach (CaptureLoop l in Loops) l.Frozen = false;
            }
        });
    }

    public string StatusText()
    {
        if (Paused) return "paused";
        Snapshot? s = Store.Current;
        string snap = s == null ? "no snapshot" : $"snapshot {(int)(DateTime.UtcNow - s.Header.TakenUtc).TotalSeconds} s ago ({s.Images.Count} outputs)";
        int ready = Loops.Count(l => l.HasFrame);
        string hook = Hotkeys.Installed ? $"{Hotkeys.ComboCount} hotkeys" : "hotkey hook unavailable";
        return $"{snap}; {ready}/{Loops.Count} outputs live; {hook}";
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        Hotkeys.Dispose();
        StopLoops();
    }
}
