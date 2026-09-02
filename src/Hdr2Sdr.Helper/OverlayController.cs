using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.ShareX;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Helper;

/// <summary>
/// On a ShareX capture hotkey: swallow the key, freeze the HDR frame, show it tonemapped in a borderless topmost
/// window over each HDR output, wait for the compositor, then start the same ShareX job through ShareX's command
/// line. ShareX's own capture then contains correct SDR pixels and every ShareX feature works unchanged.
/// </summary>
public sealed class OverlayController : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public KeyboardInput ki; public long padding; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Jobs that capture the instant they start (no selector window): hide the overlay soon after.</summary>
    private static readonly HashSet<string> InstantJobs = new(StringComparer.OrdinalIgnoreCase) { "PrintScreen", "ActiveWindow", "ActiveMonitor", "WindowRectangle", "LastRegion", "CustomRegion" };
    private const int RegionJobTimeoutMs = 2500, InstantJobTimeoutMs = 400;

    private readonly HelperService _service;
    private readonly Control _ui;
    private readonly List<OverlayForm> _forms = new();
    private readonly System.Windows.Forms.Timer _hide;
    private readonly Stopwatch _sw = new();
    private readonly Action _onRegionWindow;
    private DateTime _lastTrigger;

    public OverlayController(HelperService service, Control uiThreadControl)
    {
        _service = service;
        _ui = uiThreadControl;
        _hide = new System.Windows.Forms.Timer { Interval = RegionJobTimeoutMs };
        _hide.Tick += (_, _) => Hide("timeout");
        _service.Hotkeys.Swallow = true;
        _service.Hotkeys.Hotkey += OnHotkey;
        _onRegionWindow = () => { if (!_ui.IsDisposed) _ui.BeginInvoke(() => Hide("ShareX region window shown")); };
        _service.RegionWindow.RegionWindowShown += _onRegionWindow;
        _service.Log.Info("overlay on: ShareX hotkeys are intercepted, the corrected frame is shown and the ShareX job started");
    }

    private void OnHotkey(HotkeyCombo combo)
    {
        // Hook thread: freeze immediately, then do everything else on the UI thread.
        DateTime now = DateTime.UtcNow;
        if ((now - _lastTrigger).TotalMilliseconds < 700) return;
        _lastTrigger = now;
        foreach (CaptureLoop l in _service.Loops) l.Freeze();
        _sw.Restart();
        _ui.BeginInvoke(() => ShowAndTrigger(combo));
    }

    /// <summary>Overlay-then-trigger for a job started from the tray menu or the command line.</summary>
    public void Capture(string job)
    {
        _lastTrigger = DateTime.UtcNow;
        foreach (CaptureLoop l in _service.Loops) l.Freeze();
        _sw.Restart();
        var combo = new HotkeyCombo(0, false, false, false, false, job);
        if (_ui.InvokeRequired) _ui.BeginInvoke(() => ShowAndTrigger(combo)); else ShowAndTrigger(combo);
    }

    private void ShowAndTrigger(HotkeyCombo combo)
    {
        try
        {
            Hide("new capture");
            long t0 = _sw.ElapsedMilliseconds;
            Core.Config.Settings settings = _service.Settings;
            foreach (CaptureLoop loop in _service.Loops)
            {
                if (!loop.Output.Hdr) continue;
                FloatImage? frame = loop.Snapshot();
                if (frame == null) { _service.Log.Warn($"overlay: no frame for {loop.Output.DeviceName}"); continue; }
                _service.LastFrozen[loop.Output.DeviceName] = frame;
                ITonemapper tm = TonemapperFactory.Create(settings.Tonemap, settings.ToTonemapParams(loop.Output.SdrWhiteNits, loop.Output.MaxLuminance));
                byte[] rgba = PixelConvert.ToRgba8(frame, tm);
                var form = new OverlayForm(new Rectangle(loop.Output.Left, loop.Output.Top, loop.Output.Width, loop.Output.Height), ToBitmap(rgba, loop.Output.Width, loop.Output.Height));
                form.Show();
                _forms.Add(form);
            }
            _service.LastCaptureUtc = DateTime.UtcNow;
            long t1 = _sw.ElapsedMilliseconds;
            Application.DoEvents();          // let the forms paint
            DwmFlush(); DwmFlush();          // two composed frames so GDI's copy of the desktop contains the overlay
            long t2 = _sw.ElapsedMilliseconds;
            string how = TriggerShareX(combo);
            _hide.Stop();
            _hide.Interval = InstantJobs.Contains(combo.Job) ? InstantJobTimeoutMs : RegionJobTimeoutMs;
            _hide.Start();
            _service.Log.Info($"{combo.Job}: {_forms.Count} overlay windows, tonemap+show {t1 - t0} ms, composed after {t2 - t0} ms, ShareX started via {how} at {_sw.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            _service.Log.Error("overlay failed: " + e.Message);
            Hide("error");
            TriggerShareX(combo);   // never leave the user's capture unstarted
        }
        finally
        {
            foreach (CaptureLoop l in _service.Loops) l.Unfreeze();   // "latest" may follow the desktop again; the overlay holds its own copy
        }
    }

    private string TriggerShareX(HotkeyCombo combo)
    {
        if (StartShareXJob(combo.Job, _service.Log)) return $"ShareX.exe -{combo.Job}";
        if (combo.VirtualKey == 0) return "nothing (ShareX not found)";
        Reinject(combo);
        return "re-injected hotkey";
    }

    /// <summary>Starts a ShareX job through its command line (handed to the running instance). False if ShareX cannot be found.</summary>
    public static bool StartShareXJob(string job, HelperLog log)
    {
        try
        {
            string? exe = Process.GetProcessesByName("ShareX").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(f => f != null);
            exe ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShareX", "ShareX.exe");
            if (!File.Exists(exe)) { log.Warn("ShareX.exe not found"); return false; }
            Process.Start(new ProcessStartInfo(exe, "-" + job) { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
        catch (Exception e)
        {
            log.Warn("could not start ShareX: " + e.Message);
            return false;
        }
    }

    private static void Reinject(HotkeyCombo combo)
    {
        var inputs = new[]
        {
            new Input { type = 1, ki = new KeyboardInput { wVk = (ushort)combo.VirtualKey } },
            new Input { type = 1, ki = new KeyboardInput { wVk = (ushort)combo.VirtualKey, dwFlags = KeyEventKeyUp } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private void Hide(string why)
    {
        if (_forms.Count == 0) return;
        _hide.Stop();
        foreach (OverlayForm f in _forms) { f.Close(); f.Dispose(); }
        _forms.Clear();
        _service.Log.Info($"overlay hidden ({why}) at {_sw.ElapsedMilliseconds} ms");
    }

    private static Bitmap ToBitmap(byte[] rgba, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) { int s = (y * w + x) * 4; row[x * 4] = rgba[s + 2]; row[x * 4 + 1] = rgba[s + 1]; row[x * 4 + 2] = rgba[s]; row[x * 4 + 3] = 255; }
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    public void Dispose()
    {
        _service.Hotkeys.Swallow = false;
        _service.Hotkeys.Hotkey -= OnHotkey;
        _service.RegionWindow.RegionWindowShown -= _onRegionWindow;
        Hide("overlay off");
        _hide.Dispose();
        _service.Log.Info("overlay off");
    }

    /// <summary>Borderless, topmost, non-activating window that paints one bitmap.</summary>
    private sealed class OverlayForm : Form
    {
        private readonly Bitmap _bitmap;
        public OverlayForm(Rectangle bounds, Bitmap bitmap)
        {
            _bitmap = bitmap;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/; return cp; }
        }
        protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(_bitmap, 0, 0);
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void Dispose(bool disposing) { if (disposing) _bitmap.Dispose(); base.Dispose(disposing); }
    }
}
