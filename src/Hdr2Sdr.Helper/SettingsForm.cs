using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Hdr2Sdr.Core.Config;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;
using Hdr2Sdr.Core.Tonemap;
using Hdr2Sdr.Windows.Imaging;

namespace Hdr2Sdr.Helper;

/// <summary>Edits settings.json with a live preview of the last processed capture.</summary>
public sealed class SettingsForm : Form
{
    private readonly HelperService _service;
    private readonly ComboBox _tonemap = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TrackBar _exposureBar = new() { Minimum = 0, Maximum = 200, TickFrequency = 25, Width = 220 };
    private readonly NumericUpDown _exposure = new() { DecimalPlaces = 2, Minimum = 0.01m, Maximum = 16m, Increment = 0.05m, Width = 70 };
    private readonly TrackBar _kneeBar = new() { Minimum = 10, Maximum = 100, TickFrequency = 10, Width = 220 };
    private readonly Label _kneeLabel = new() { AutoSize = true };
    private readonly CheckBox _sdrOverride = new() { Text = "Override SDR white (nits)", AutoSize = true };
    private readonly NumericUpDown _sdrWhite = new() { Minimum = 40, Maximum = 2000, Width = 70 };
    private readonly CheckBox _peakOverride = new() { Text = "Override peak (nits)", AutoSize = true };
    private readonly NumericUpDown _peak = new() { Minimum = 100, Maximum = 10000, Width = 70 };
    private readonly Label _monitorInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ComboBox _cursor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _jpeg = new() { DecimalPlaces = 2, Minimum = 0.1m, Maximum = 1m, Increment = 0.05m, Width = 70 };
    private readonly NumericUpDown _webp = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly CheckBox _webpLossless = new() { Text = "lossless", AutoSize = true };
    private readonly CheckBox _sidecar = new() { Text = "Also save the raw HDR region as JPEG XR (.jxr)", AutoSize = true };
    private readonly CheckBox _useHelper = new() { Text = "Use helper snapshots (exact frame at the hotkey)", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Start helper at logon (scheduled task)", AutoSize = true };
    private readonly CheckBox _keyboardHook = new() { Text = "Watch ShareX hotkeys with a keyboard hook", AutoSize = true };
    private readonly NumericUpDown _history = new() { Minimum = 0, Maximum = 1000, Increment = 50, Width = 70 };
    private readonly Label _historyNote = new() { Text = "ms of frame history kept on the GPU (0 = off); aligns tray/CLI captures, ~40 MB VRAM per 16 ms at 4K", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly PictureBox _original = new() { SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black };
    private readonly PictureBox _preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black };
    private readonly Label _previewStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private bool _loading;

    public SettingsForm(HelperService service)
    {
        _service = service;
        Text = "hdr2sdr settings";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(900, 620);
        Size = new Size(1000, 680);

        _tonemap.Items.AddRange(TonemapperFactory.Names);
        _cursor.Items.AddRange(Settings.CursorModes);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control a, Control? b = null)
        {
            int r = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) }, 0, r);
            grid.Controls.Add(a, 1, r);
            if (b != null) grid.Controls.Add(b, 2, r);
        }
        Row("Tonemap", _tonemap);
        Row("Exposure", _exposureBar, _exposure);
        Row("Knee (roll-off start, 1.0 = exact SDR)", _kneeBar, _kneeLabel);
        Row("", _sdrOverride, _sdrWhite);
        Row("", _peakOverride, _peak);
        Row("", _monitorInfo);
        Row("Cursor", _cursor);
        Row("JPEG quality", _jpeg);
        Row("WebP quality", _webp, _webpLossless);
        Row("", _sidecar);
        Row("", _useHelper);
        Row("", _keyboardHook);
        Row("Frame history", _history, _historyNote);
        Row("", _startup);

        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.Controls.Add(new Label { Text = "Last capture as ShareX saved it", AutoSize = true }, 0, 0);
        previewPanel.Controls.Add(new Label { Text = "Re-tonemapped with these settings", AutoSize = true }, 1, 0);
        _original.Dock = DockStyle.Fill; _preview.Dock = DockStyle.Fill;
        previewPanel.Controls.Add(_original, 0, 1);
        previewPanel.Controls.Add(_preview, 1, 1);
        previewPanel.Controls.Add(_previewStatus, 0, 2);
        previewPanel.SetColumnSpan(_previewStatus, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => { Save(); Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AcceptButton = save; CancelButton = cancel;

        Controls.Add(previewPanel);
        Controls.Add(grid);
        Controls.Add(buttons);

        _exposureBar.ValueChanged += (_, _) => { if (!_loading) { _loading = true; _exposure.Value = (decimal)Math.Round(MathF.Pow(2f, (_exposureBar.Value - 100) / 50f), 2); _loading = false; Changed(); } };
        _exposure.ValueChanged += (_, _) => { if (!_loading) { _loading = true; _exposureBar.Value = Math.Clamp((int)MathF.Round(MathF.Log2((float)_exposure.Value) * 50f + 100f), 0, 200); _loading = false; Changed(); } };
        _kneeBar.ValueChanged += (_, _) => { _kneeLabel.Text = (_kneeBar.Value / 100f).ToString("0.00"); Changed(); };
        foreach (Control c in new Control[] { _tonemap, _cursor, _sdrOverride, _sdrWhite, _peakOverride, _peak, _jpeg, _webp, _webpLossless, _sidecar, _useHelper })
        {
            if (c is ComboBox cb) cb.SelectedIndexChanged += (_, _) => Changed();
            else if (c is CheckBox ck) ck.CheckedChanged += (_, _) => Changed();
            else if (c is NumericUpDown nu) nu.ValueChanged += (_, _) => Changed();
        }
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };

        Load();
        ShowMonitorInfo();
        LoadOriginal();
        RenderPreview();
    }

    private void Changed()
    {
        if (_loading) return;
        _sdrWhite.Enabled = _sdrOverride.Checked;
        _peak.Enabled = _peakOverride.Checked;
        _webp.Enabled = !_webpLossless.Checked;
        _debounce.Stop();
        _debounce.Start();
    }

    private Settings Current() => new()
    {
        Tonemap = _tonemap.SelectedItem?.ToString() ?? "desktop",
        Exposure = (float)_exposure.Value,
        Knee = _kneeBar.Value / 100f,
        SdrWhiteNits = _sdrOverride.Checked ? (float)_sdrWhite.Value : null,
        PeakNits = _peakOverride.Checked ? (float)_peak.Value : null,
        JpegQuality = (float)_jpeg.Value,
        WebpQuality = _webpLossless.Checked ? Settings.WebpLossless : (int)_webp.Value,
        Cursor = _cursor.SelectedItem?.ToString() ?? "auto",
        HdrSidecar = _sidecar.Checked ? "jxr" : "none",
        UseHelper = _useHelper.Checked,
        HelperKeyboardHook = _keyboardHook.Checked,
        HelperHistoryMs = (int)_history.Value,
        CarryAnnotations = _carry,
        HelperRingMs = _ringMs,
        HelperRingFrames = _ringFrames,
    };
    private bool _carry = true;
    private int _ringMs = 250, _ringFrames = 12;

    private new void Load()
    {
        _loading = true;
        var (s, _) = SettingsFile.Load(ShareXPaths.SettingsPath);
        _tonemap.SelectedItem = s.Tonemap;
        _exposure.Value = (decimal)Math.Clamp(s.Exposure, 0.01f, 16f);
        _exposureBar.Value = Math.Clamp((int)MathF.Round(MathF.Log2(s.Exposure) * 50f + 100f), 0, 200);
        _kneeBar.Value = Math.Clamp((int)MathF.Round(s.Knee * 100f), 10, 100);
        _kneeLabel.Text = (_kneeBar.Value / 100f).ToString("0.00");
        _sdrOverride.Checked = s.SdrWhiteNits != null;
        _sdrWhite.Value = (decimal)Math.Clamp(s.SdrWhiteNits ?? 200f, 40f, 2000f);
        _peakOverride.Checked = s.PeakNits != null;
        _peak.Value = (decimal)Math.Clamp(s.PeakNits ?? 1000f, 100f, 10000f);
        _cursor.SelectedItem = s.Cursor;
        _jpeg.Value = (decimal)s.JpegQuality;
        _webpLossless.Checked = s.WebpQuality == Settings.WebpLossless;
        _webp.Value = _webpLossless.Checked ? 90 : s.WebpQuality;
        _sidecar.Checked = s.HdrSidecar == "jxr";
        _useHelper.Checked = s.UseHelper;
        _keyboardHook.Checked = s.HelperKeyboardHook;
        _history.Value = Math.Clamp(s.HelperHistoryMs, 0, 1000);
        _carry = s.CarryAnnotations; _ringMs = s.HelperRingMs; _ringFrames = s.HelperRingFrames;   // not shown, preserved on save
        _startup.Checked = StartupTask.IsInstalled();
        _sdrWhite.Enabled = _sdrOverride.Checked;
        _peak.Enabled = _peakOverride.Checked;
        _webp.Enabled = !_webpLossless.Checked;
        _loading = false;
    }

    private void Save()
    {
        Settings s = Current().Sanitized(out _);
        SettingsFile.Save(ShareXPaths.SettingsPath, s);
        _service.Log.Info("settings saved");
        _service.ReloadSettings();
        bool installed = StartupTask.IsInstalled();
        if (_startup.Checked && !installed)
        {
            string err = StartupTask.Install(Environment.ProcessPath ?? Application.ExecutablePath);
            if (err.Length > 0) MessageBox.Show(this, "Could not create the logon task:\n" + err, "hdr2sdr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (!_startup.Checked && installed)
        {
            string err = StartupTask.Remove();
            if (err.Length > 0) MessageBox.Show(this, "Could not remove the logon task:\n" + err, "hdr2sdr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowMonitorInfo()
    {
        var hdr = _service.Loops.Select(l => l.Output).Where(o => o.Hdr).ToList();
        _monitorInfo.Text = hdr.Count == 0
            ? "No HDR output detected right now."
            : "Monitor values: " + string.Join("; ", hdr.Select(o => $"{o.DeviceName} SDR white {o.SdrWhiteNits:F0} nits, peak {o.MaxLuminance:F0} nits"));
    }

    private void LoadOriginal()
    {
        LastRegion? r = _service.Store.LastRegion;
        if (r == null || !File.Exists(r.InputPath)) { _original.Image = null; return; }
        Task.Run(() =>
        {
            try
            {
                var (rgba, w, h) = ImageIO.Load(r.InputPath);
                Bitmap bmp = BitmapFromRgba(rgba, w, h);
                if (IsDisposed) { bmp.Dispose(); return; }
                BeginInvoke(() => { Image? old = _original.Image; _original.Image = bmp; old?.Dispose(); });
            }
            catch (Exception e)
            {
                _service.Log.Warn($"preview: cannot load original: {e.Message}");
            }
        });
    }

    private int _renderVersion;

    /// <summary>Renders on a background thread so the UI (and anything sharing it) never stalls.</summary>
    private void RenderPreview()
    {
        LastRegion? r = _service.Store.LastRegion;
        Snapshot? snap = _service.Store.LastForPreview ?? _service.Store.Current;
        if (r == null || snap == null)
        {
            _previewStatus.Text = "Take a capture with ShareX first; the last region will show here.";
            _preview.Image = null;
            return;
        }
        Settings s = Current().Sanitized(out _);
        int version = Interlocked.Increment(ref _renderVersion);
        _previewStatus.Text = "Rendering...";
        Task.Run(() =>
        {
            try
            {
                var previewTiles = new List<RgbaImage.Tile>();
                for (int i = 0; i < snap.Header.Outputs.Count; i++)
                {
                    SnapshotOutput o = snap.Header.Outputs[i];
                    bool intersects = r.Left < o.Left + o.Width && r.Left + r.Width > o.Left && r.Top < o.Top + o.Height && r.Top + r.Height > o.Top;
                    if (!intersects) continue;
                    // Only the region needs tonemapping: crop first when the region sits inside this output.
                    bool inside = r.Left >= o.Left && r.Top >= o.Top && r.Left + r.Width <= o.Left + o.Width && r.Top + r.Height <= o.Top + o.Height;
                    ITonemapper tm = o.Hdr
                        ? TonemapperFactory.Create(s.Tonemap, s.ToTonemapParams(o.SdrWhiteNits, o.PeakNits))
                        : new DesktopTonemapper(new TonemapParams { SdrWhiteNits = o.SdrWhiteNits, PeakNits = o.PeakNits });
                    if (inside)
                    {
                        FloatImage crop = snap.Images[i].Crop(r.Left - o.Left, r.Top - o.Top, r.Width, r.Height);
                        previewTiles.Add(new RgbaImage.Tile(PixelConvert.ToRgba8(crop, tm), r.Width, r.Height, r.Left, r.Top));
                        break;
                    }
                    previewTiles.Add(new RgbaImage.Tile(PixelConvert.ToRgba8(snap.Images[i], tm), o.Width, o.Height, o.Left, o.Top));
                }
                if (previewTiles.Count == 0) { Post(version, null, "The last region is not inside the snapshot."); return; }
                RgbaImage.Canvas canvas = RgbaImage.Composite(previewTiles);
                int x = r.Left - canvas.Left, y = r.Top - canvas.Top;
                if (x < 0 || y < 0 || x + r.Width > canvas.Width || y + r.Height > canvas.Height) { Post(version, null, "The last region is not inside the snapshot."); return; }
                byte[] rgba = RgbaImage.Crop(canvas.Rgba, canvas.Width, canvas.Height, x, y, r.Width, r.Height);
                Bitmap bmp = BitmapFromRgba(rgba, r.Width, r.Height);
                Post(version, bmp, $"Region {r.Width}x{r.Height} at ({r.Left},{r.Top}), captured {(int)(DateTime.UtcNow - snap.Header.TakenUtc).TotalSeconds} s ago. Settings apply to the next capture after Save.");
            }
            catch (Exception e)
            {
                Post(version, null, "Preview failed: " + e.Message);
            }
        });
    }

    private void Post(int version, Bitmap? bmp, string status)
    {
        if (IsDisposed) { bmp?.Dispose(); return; }
        try
        {
            BeginInvoke(() =>
            {
                if (version != _renderVersion) { bmp?.Dispose(); return; }   // a newer render superseded this one
                Image? old = _preview.Image;
                _preview.Image = bmp;
                old?.Dispose();
                _previewStatus.Text = status;
            });
        }
        catch (InvalidOperationException)
        {
            bmp?.Dispose();
        }
    }

    private static Bitmap BitmapFromRgba(byte[] rgba, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4;
                    row[x * 4] = rgba[s + 2]; row[x * 4 + 1] = rgba[s + 1]; row[x * 4 + 2] = rgba[s]; row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
