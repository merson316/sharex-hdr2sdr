using System.Windows.Forms;
using Hdr2Sdr.Windows.Display;

namespace Hdr2Sdr.Helper;

/// <summary>Owns the capture loops, hotkey watcher, snapshot store and pipe server.</summary>
public sealed class HelperService : IDisposable
{
    public HelperLog Log { get; } = new();
    public SnapshotStore Store { get; }
    public HotkeyWatcher Hotkeys { get; }
    public RegionWindowWatcher RegionWindow { get; }
    public List<CaptureLoop> Loops { get; } = new();
    private Core.Config.Settings _settings = new();
    public Core.Config.Settings Settings => _settings;
    private OverlayController? _overlay;
    private Control? _ui;
    /// <summary>Command-line override: true/false forces overlay mode on/off regardless of settings.</summary>
    public bool? OverlayOverride { get; set; }
    public bool OverlayOn => _overlay != null;
    private DisplaySet? _displays;
    private PipeServer? _pipe;
    public bool Paused { get => Hotkeys.Paused; set { Hotkeys.Paused = value; Log.Info(value ? "paused" : "resumed"); } }

    public HelperService()
    {
        Store = new SnapshotStore(Log);
        Hotkeys = new HotkeyWatcher(ShareXPaths.HotkeysConfig, Log);
        Hotkeys.Hotkey += OnHotkey;
        RegionWindow = new RegionWindowWatcher(Log);
        RegionWindow.RegionWindowShown += () => Trigger("region window");
        Hotkeys.OnHookThreadReady = () => RegionWindow.Install();
        ReloadSettings();
    }

    /// <summary>Re-reads settings.json and applies the helper-related values (history, keyboard hook, overlay mode).</summary>
    public void ReloadSettings()
    {
        (_settings, _) = Core.Config.SettingsFile.Load(ShareXPaths.SettingsPath);
        Hotkeys.KeyboardHookEnabled = _settings.HelperKeyboardHook;
        foreach (CaptureLoop l in Loops) l.HistoryMs = _settings.HelperHistoryMs;
        ApplyOverlaySetting();
    }

    /// <summary>Call once from the UI thread with a control created there; overlay windows are shown through it.</summary>
    public void AttachUi(Control ui)
    {
        _ui = ui;
        ApplyOverlaySetting();
    }

    private void ApplyOverlaySetting()
    {
        bool want = OverlayOverride ?? (_settings.OverlayMode && _settings.HelperKeyboardHook);
        if (_ui == null) return;
        void Apply()
        {
            if (want && _overlay == null) _overlay = new OverlayController(this, _ui);
            else if (!want && _overlay != null) { _overlay.Dispose(); _overlay = null; }
        }
        if (_ui.InvokeRequired) _ui.BeginInvoke(Apply); else Apply();
    }

    /// <summary>Starts loops and the pipe server. Call Hotkeys.Install() from the UI thread separately.</summary>
    public void Start()
    {
        StartLoops();
        _pipe = new PipeServer(Store, () => Loops, StatusText, Log);
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
            var loop = new CaptureLoop(o, Log) { HistoryMs = _settings.HelperHistoryMs };
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

    private void OnHotkey(Core.Snapshot.HotkeyCombo combo) => Trigger("hotkey " + combo.Job);

    private DateTime _lastTrigger;

    private void Trigger(string source)
    {
        // Runs on the hook thread: freeze and start the ring immediately, then snapshot on the pool so the hook returns at once.
        DateTime now = DateTime.UtcNow;
        if ((now - _lastTrigger).TotalMilliseconds < 700) return;   // hotkey and the window it opens are one capture
        _lastTrigger = now;
        Core.Config.Settings settings = _settings;
        foreach (CaptureLoop l in Loops) l.BeginRecording(settings.HelperRingMs, settings.HelperRingFrames);
        Task.Run(() =>
        {
            try
            {
                bool cursor = CursorPolicy.IncludeCursor(ShareXPaths.SettingsPath, ShareXPaths.ApplicationConfig);
                Store.Take(Loops, cursor);
                Log.Info($"{source}: snapshot taken (cursor={cursor}), ring {Loops.Sum(l => l.RingCount)} history frames + {settings.HelperRingMs} ms / {settings.HelperRingFrames} frames");
            }
            catch (Exception e)
            {
                Log.Error($"snapshot failed: {e.Message}");
                foreach (CaptureLoop l in Loops) l.Unfreeze();
            }
        });
    }

    public string StatusText()
    {
        if (Paused) return "paused";
        Snapshot? s = Store.Current;
        string snap = s == null ? "no snapshot" : $"snapshot {(int)(DateTime.UtcNow - s.Header.TakenUtc).TotalSeconds} s ago ({s.Images.Count} outputs)";
        int ready = Loops.Count(l => l.HasFrame);
        string hook = Hotkeys.Installed ? $"{Hotkeys.ComboCount} hotkeys" : (Hotkeys.KeyboardHookEnabled ? "hotkey hook unavailable" : "hotkey hook off");
        string hist = _settings.HelperHistoryMs > 0 ? $"; history {_settings.HelperHistoryMs} ms ({Loops.Sum(l => l.HistoryCount)} frames)" : "";
        string mode = OverlayOn ? "overlay mode" : "post-capture mode";
        return $"{mode}; {snap}; {ready}/{Loops.Count} outputs live; {hook}{(RegionWindow.Installed ? ", window watch" : "")}{hist}";
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _overlay?.Dispose();
        RegionWindow.Dispose();
        Hotkeys.Dispose();
        StopLoops();
    }
}
