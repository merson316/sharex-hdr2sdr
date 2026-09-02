using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Hdr2Sdr.Core.Config;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Tonemap;
using Hdr2Sdr.Windows.Display;

namespace Hdr2Sdr.Helper;

/// <summary>Edits settings.json with a live preview of the last frozen frame.</summary>
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
    private readonly CheckBox _startup = new() { Text = "Start at logon (scheduled task)", AutoSize = true };
    private readonly PictureBox _preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black, Dock = DockStyle.Fill };
    private readonly Label _previewStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private bool _loading;
    private int _renderVersion;

    public SettingsForm(HelperService service)
    {
        _service = service;
        Text = "hdr2sdr settings";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(820, 600);
        Size = new Size(900, 660);

        _tonemap.Items.AddRange(TonemapperFactory.Names);

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
        Row("", _startup);

        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.Controls.Add(new Label { Text = "Last frozen frame, tonemapped with these settings", AutoSize = true }, 0, 0);
        previewPanel.Controls.Add(_preview, 0, 1);
        previewPanel.Controls.Add(_previewStatus, 0, 2);

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
        _tonemap.SelectedIndexChanged += (_, _) => Changed();
        foreach (CheckBox c in new[] { _sdrOverride, _peakOverride }) c.CheckedChanged += (_, _) => Changed();
        foreach (NumericUpDown n in new[] { _sdrWhite, _peak }) n.ValueChanged += (_, _) => Changed();
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };

        Load();
        ShowMonitorInfo();
        RenderPreview();
    }

    private void Changed()
    {
        if (_loading) return;
        _sdrWhite.Enabled = _sdrOverride.Checked;
        _peak.Enabled = _peakOverride.Checked;
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
    };

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
        _startup.Checked = StartupTask.IsInstalled();
        _sdrWhite.Enabled = _sdrOverride.Checked;
        _peak.Enabled = _peakOverride.Checked;
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

    /// <summary>Renders the last frozen frame of the first HDR output on a background thread.</summary>
    private void RenderPreview()
    {
        OutputHandle? o = _service.Loops.Select(l => l.Output).FirstOrDefault(x => x.Hdr && _service.LastFrozen.ContainsKey(x.DeviceName));
        if (o == null)
        {
            _previewStatus.Text = "Take a capture first; the frame that was shown to ShareX will appear here.";
            _preview.Image = null;
            return;
        }
        FloatImage frame = _service.LastFrozen[o.DeviceName];
        Settings s = Current().Sanitized(out _);
        int version = Interlocked.Increment(ref _renderVersion);
        _previewStatus.Text = "Rendering...";
        Task.Run(() =>
        {
            try
            {
                // Preview at half resolution: plenty for judging the curve, four times faster.
                FloatImage small = Downsample2(frame);
                ITonemapper tm = TonemapperFactory.Create(s.Tonemap, s.ToTonemapParams(o.SdrWhiteNits, o.MaxLuminance));
                byte[] rgba = PixelConvert.ToRgba8(small, tm);
                Bitmap bmp = BitmapFromRgba(rgba, small.Width, small.Height);
                Post(version, bmp, $"{o.DeviceName}, captured {(int)(DateTime.UtcNow - _service.LastCaptureUtc).TotalSeconds} s ago. Settings apply to the next capture after Save.");
            }
            catch (Exception e)
            {
                Post(version, null, "Preview failed: " + e.Message);
            }
        });
    }

    private static FloatImage Downsample2(FloatImage src)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var dst = new FloatImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int i0 = ((2 * y) * src.Width + 2 * x) * 3 + c, i1 = i0 + 3, i2 = i0 + src.Width * 3, i3 = i2 + 3;
                    dst.Data[(y * w + x) * 3 + c] = 0.25f * (src.Data[i0] + src.Data[i1] + src.Data[i2] + src.Data[i3]);
                }
        return dst;
    }

    private void Post(int version, Bitmap? bmp, string status)
    {
        if (IsDisposed) { bmp?.Dispose(); return; }
        try
        {
            BeginInvoke(() =>
            {
                if (version != _renderVersion) { bmp?.Dispose(); return; }
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
