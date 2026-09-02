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

    public TrayApp(HelperService service)
    {
        _service = service;
        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("starting") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        _pause = new ToolStripMenuItem("Pause hotkey freeze", null, (_, _) => { _service.Paused = !_service.Paused; _pause.Checked = _service.Paused; });
        menu.Items.Add(_pause);
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(_service.Log.Path) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = AppIcon.Get(), Text = "hdr2sdr helper", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => _status.Text = _service.StatusText();
        _timer.Start();
        _window = new DisplayChangeWindow(() => _service.OnDisplayChange());
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

    /// <summary>Invisible window that receives WM_DISPLAYCHANGE.</summary>
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
