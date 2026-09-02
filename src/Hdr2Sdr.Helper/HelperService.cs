using System.Windows.Forms;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.ShareX;
using Hdr2Sdr.Windows.Display;

namespace Hdr2Sdr.Helper;

/// <summary>Owns the capture loops, the hotkey watcher, the overlay and the pipe server.</summary>
public sealed class HelperService : IDisposable
{
    public HelperLog Log { get; } = new();
    public HotkeyWatcher Hotkeys { get; }
    public RegionWindowWatcher RegionWindow { get; }
    public List<CaptureLoop> Loops { get; } = new();
    public Core.Config.Settings Settings { get; private set; } = new();
    /// <summary>The frames last shown as overlay, per output, for the settings preview.</summary>
    public Dictionary<string, FloatImage> LastFrozen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastCaptureUtc { get; set; }

    private DisplaySet? _displays;
    private PipeServer? _pipe;
    private OverlayController? _overlay;
    private Control? _ui;
    public bool OverlayOn => _overlay != null;
    public bool Paused { get => Hotkeys.Paused; set { Hotkeys.Paused = value; Log.Info(value ? "paused: ShareX captures on its own" : "resumed"); } }

    public HelperService()
    {
        Hotkeys = new HotkeyWatcher(ShareXPaths.HotkeysConfig, Log);
        RegionWindow = new RegionWindowWatcher(Log);
        Hotkeys.OnHookThreadReady = () => RegionWindow.Install();
        ReloadSettings();
    }

    public void Start()
    {
        StartLoops();
        _pipe = new PipeServer(StatusText, StartCapture, Log);
        _pipe.Start();
        Log.Info("hdr2sdr started");
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
            Log.Info("output " + o);
        }
    }

    private void StopLoops()
    {
        foreach (CaptureLoop l in Loops) l.Dispose();
        Loops.Clear();
        _displays?.Dispose();
        _displays = null;
    }

    public void OnDisplayChange()
    {
        Log.Info("display change: restarting capture loops");
        Task.Run(StartLoops);
    }

    /// <summary>Re-reads settings.json and applies hook/overlay state.</summary>
    public void ReloadSettings()
    {
        (Settings, string? err) = Core.Config.SettingsFile.Load(ShareXPaths.SettingsPath);
        if (err != null) Log.Warn("settings: " + err);
        Hotkeys.KeyboardHookEnabled = Settings.InterceptHotkeys;
        ApplyOverlay();
    }

    /// <summary>Call once from the UI thread with a control created there; overlay windows are shown through it.</summary>
    public void AttachUi(Control ui)
    {
        _ui = ui;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        if (_ui == null) return;
        bool want = Settings.InterceptHotkeys;
        void Apply()
        {
            if (want && _overlay == null) _overlay = new OverlayController(this, _ui);
            else if (!want && _overlay != null) { _overlay.Dispose(); _overlay = null; }
        }
        if (_ui.InvokeRequired) _ui.BeginInvoke(Apply); else Apply();
    }

    /// <summary>Starts a ShareX capture job from the helper (tray menu or command line): freeze, overlay, then the ShareX job.</summary>
    public void StartCapture(string job)
    {
        if (!ShareXHotkeys.CaptureJobs.Contains(job)) { Log.Warn($"unknown capture job '{job}'"); return; }
        if (_overlay != null) _overlay.Capture(job);
        else OverlayController.StartShareXJob(job, Log);
    }

    public string StatusText()
    {
        if (Paused) return "paused: ShareX captures on its own";
        int ready = Loops.Count(l => l.HasFrame);
        string hook = Hotkeys.Installed ? $"{Hotkeys.ComboCount} ShareX hotkeys intercepted" : (Hotkeys.KeyboardHookEnabled ? "hotkey hook unavailable" : "hotkeys not intercepted");
        string last = LastCaptureUtc == default ? "no capture yet" : $"last capture {(int)(DateTime.UtcNow - LastCaptureUtc).TotalSeconds} s ago";
        return $"{hook}; {ready}/{Loops.Count} HDR/SDR outputs live; {last}";
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
