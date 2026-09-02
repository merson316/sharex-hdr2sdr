using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Helper;

/// <summary>
/// SPIKE: on a ShareX hotkey, swallow the key, show our tonemapped frame as a borderless topmost window over each
/// HDR output, wait for the compositor, then re-inject the hotkey so ShareX captures the overlay with GDI.
/// Throwaway code to answer two questions: does GDI see the overlay in time, and does ShareX accept the re-injected key?
/// </summary>
public sealed class OverlaySpike : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public KeyboardInput ki; public long padding; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private const uint KeyEventKeyUp = 0x0002;

    private readonly HelperService _service;
    private readonly Control _ui;
    private readonly List<OverlayForm> _forms = new();
    private readonly System.Windows.Forms.Timer _hide;
    private readonly Stopwatch _sw = new();

    public OverlaySpike(HelperService service, Control uiThreadControl)
    {
        _service = service;
        _ui = uiThreadControl;
        _hide = new System.Windows.Forms.Timer { Interval = 2500 };
        _hide.Tick += (_, _) => Hide("timeout");
        _service.Hotkeys.Swallow = true;
        _service.Hotkeys.Hotkey += OnHotkey;
        _service.RegionWindow.RegionWindowShown += () => _ui.BeginInvoke(() => Hide("ShareX region window shown"));
        _service.Log.Info("overlay spike active: ShareX hotkeys are swallowed, overlaid and re-injected");
    }

    private void OnHotkey(HotkeyCombo combo)
    {
        _sw.Restart();
        // The service has already frozen the frame; do the rest on the UI thread.
        _ui.BeginInvoke(() => ShowAndReinject(combo));
    }

    private void ShowAndReinject(HotkeyCombo combo)
    {
        try
        {
            Hide("new hotkey");
            long t0 = _sw.ElapsedMilliseconds;
            foreach (CaptureLoop loop in _service.Loops)
            {
                if (!loop.Output.Hdr) continue;
                var shot = loop.Snapshot(includeCursor: false, timeoutMs: 1000);
                if (shot == null) { _service.Log.Warn($"overlay: no frame for {loop.Output.DeviceName}"); continue; }
                var tm = new DesktopTonemapper(new TonemapParams { SdrWhiteNits = loop.Output.SdrWhiteNits, PeakNits = loop.Output.MaxLuminance });
                byte[] rgba = PixelConvert.ToRgba8(shot.Value.Image, tm);
                var form = new OverlayForm(new Rectangle(loop.Output.Left, loop.Output.Top, loop.Output.Width, loop.Output.Height), ToBitmap(rgba, loop.Output.Width, loop.Output.Height));
                form.Show();
                _forms.Add(form);
            }
            long t1 = _sw.ElapsedMilliseconds;
            Application.DoEvents();          // let the forms paint
            DwmFlush(); DwmFlush();          // two composed frames so GDI's copy of the desktop contains the overlay
            long t2 = _sw.ElapsedMilliseconds;
            Reinject(combo);
            _hide.Stop(); _hide.Start();
            _service.Log.Info($"overlay: {_forms.Count} windows, tonemap+show {t1 - t0} ms, composed after {t2 - t0} ms, hotkey re-injected at {_sw.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            _service.Log.Error("overlay failed: " + e.Message);
            Hide("error");
            Reinject(combo);   // never leave the user's hotkey swallowed
        }
    }

    private static void Reinject(HotkeyCombo combo)
    {
        // Modifiers are still physically held by the user; only the main key needs to be pressed again.
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
        Hide("dispose");
        _hide.Dispose();
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
            DoubleBuffered = true;
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
