using System.Diagnostics;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;

namespace Hdr2Sdr.Helper;

public sealed record Snapshot(SnapshotHeader Header, List<FloatImage> Images);

/// <summary>Holds the newest snapshot (frozen at a hotkey) until it is consumed or expires.</summary>
public sealed class SnapshotStore
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(120);
    private readonly object _lock = new();
    private readonly HelperLog _log;
    private Snapshot? _current;

    public SnapshotStore(HelperLog log) => _log = log;

    /// <summary>Last processed region, reported by the action for the settings preview.</summary>
    public LastRegion? LastRegion { get; set; }

    /// <summary>The snapshot the action last consumed, kept only so the settings preview can re-render it.</summary>
    public Snapshot? LastForPreview { get; private set; }

    /// <summary>Set by the overlay while it is showing frames to ShareX; stamped into every snapshot taken.</summary>
    public volatile bool OverlayActive;

    public Snapshot? Current
    {
        get
        {
            lock (_lock)
            {
                if (_current != null && DateTime.UtcNow - _current.Header.TakenUtc > Expiry) _current = null;
                return _current;
            }
        }
    }

    /// <summary>Copies every loop's frozen "latest" frame (frozen by BeginRecording at the hotkey), then unfreezes. Ring frames keep recording.</summary>
    public TimeSpan Take(IReadOnlyList<CaptureLoop> loops, bool includeCursor)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var outputs = new List<SnapshotOutput>();
            var images = new List<FloatImage>();
            foreach (CaptureLoop l in loops)
            {
                var shot = l.Snapshot(includeCursor);
                if (shot == null) { _log.Warn($"{l.Output.DeviceName}: no frame available for snapshot"); continue; }
                var o = l.Output;
                outputs.Add(new SnapshotOutput(o.DeviceName, o.Left, o.Top, o.Width, o.Height, o.Hdr, o.SdrWhiteNits, o.MaxLuminance, shot.Value.Cursor));
                images.Add(shot.Value.Image);
            }
            lock (_lock)
            {
                _current = outputs.Count > 0 ? new Snapshot(new SnapshotHeader(DateTime.UtcNow, outputs, OverlayActive), images) : null;
            }
        }
        finally
        {
            foreach (CaptureLoop l in loops) l.Unfreeze();
        }
        sw.Stop();
        _log.Info($"snapshot of {loops.Count} outputs in {sw.ElapsedMilliseconds} ms");
        return sw.Elapsed;
    }

    public void Consume(IReadOnlyList<CaptureLoop> loops)
    {
        lock (_lock)
        {
            if (_current != null) LastForPreview = _current;
            _current = null;
        }
        foreach (CaptureLoop l in loops) l.ClearRing();
    }
}

public sealed record LastRegion(string InputPath, int Left, int Top, int Width, int Height, DateTime WhenUtc);
