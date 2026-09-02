using System.Diagnostics;
using System.Windows.Forms;

namespace Hdr2Sdr.Helper;

/// <summary>Message-only context holding the tray icon and its menu.</summary>
public sealed class TrayApp : ApplicationContext
{
    private const int WmDisplayChange = 0x007E;
    private readonly HelperService _service;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _pause;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DisplayChangeWindow _window;
    private SettingsForm? _settings;
    /// <summary>A control created on the UI thread, for BeginInvoke from other threads.</summary>
    public Control UiControl { get; } = new Control();

    public TrayApp(HelperService service)
    {
        _service = service;
        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("starting") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        var capture = new ToolStripMenuItem("Capture");
        foreach (var (label, job) in new[] { ("Region", "RectangleRegion"), ("Active window", "ActiveWindow"), ("Active monitor", "ActiveMonitor"), ("Fullscreen", "PrintScreen"), ("Last region", "LastRegion") })
            capture.DropDownItems.Add(label, null, (_, _) => CaptureAfterMenuCloses(menu, job));
        menu.Items.Add(capture);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        _pause = new ToolStripMenuItem("Pause (let ShareX capture on its own)", null, (_, _) => { _service.Paused = !_service.Paused; _pause.Checked = _service.Paused; });
        menu.Items.Add(_pause);
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(_service.Log.Path) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = AppIcon.Get(), Text = "hdr2sdr", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => _status.Text = _service.StatusText();
        _timer.Start();
        _window = new DisplayChangeWindow(() => _service.OnDisplayChange());
        _ = UiControl.Handle;   // force creation on this thread
    }

    /// <summary>The menu and the taskbar's tray flyout are still on screen when an item is clicked; capture only after they are gone.</summary>
    private void CaptureAfterMenuCloses(ContextMenuStrip menu, string job)
    {
        menu.Close();
        var delay = new System.Windows.Forms.Timer { Interval = 350 };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            delay.Dispose();
            _service.StartCapture(job);
        };
        delay.Start();
    }

    private void ShowSettings()
    {
        if (_settings == null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_service);
            _settings.Show();
        }
        _settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _timer.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }

    private sealed class DisplayChangeWindow : NativeWindow, IDisposable
    {
        private readonly Action _onChange;
        public DisplayChangeWindow(Action onChange)
        {
            _onChange = onChange;
            CreateHandle(new CreateParams());
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDisplayChange) _onChange();
            base.WndProc(ref m);
        }
        public void Dispose() => DestroyHandle();
    }
}
