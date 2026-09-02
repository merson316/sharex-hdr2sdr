using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;

namespace Hdr2Sdr.Windows.Helper;

public sealed record HelperSnapshot(SnapshotHeader Header, List<FloatImage> Images);

/// <summary>
/// Client side of the helper's named pipe. Every call has a hard deadline and every failure is logged and
/// turned into "nothing": the action must never wait on the helper long enough to stall ShareX.
/// </summary>
public sealed class HelperClient
{
    public const string PipeName = "hdr2sdr-helper";
    private static readonly TimeSpan GetDeadline = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RingDeadline = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan NotifyDeadline = TimeSpan.FromMilliseconds(800);
    private readonly Action<string> _log;

    public HelperClient(Action<string> log) => _log = log;

    public HelperSnapshot? TryGetSnapshot(TimeSpan connectTimeout)
    {
        try
        {
            return Deadline(GetDeadline, async ct =>
            {
                using var pipe = await ConnectAsync(connectTimeout, ct);
                await WriteLineAsync(pipe, "{\"op\":\"get\"}", ct);
                string status = await ReadLineAsync(pipe, ct);
                using (JsonDocument doc = JsonDocument.Parse(status))
                {
                    if (!doc.RootElement.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
                    {
                        _log("helper has " + (doc.RootElement.TryGetProperty("reason", out JsonElement r) ? r.GetString() : "no snapshot"));
                        return null;
                    }
                }
                SnapshotHeader header = SnapshotHeader.FromJson(await ReadLineAsync(pipe, ct));
                var images = new List<FloatImage>(header.Outputs.Count);
                foreach (SnapshotOutput o in header.Outputs)
                {
                    byte[] buf = await ReadExactAsync(pipe, o.Width * o.Height * 3 * 2, ct);
                    images.Add(Half16Codec.Decode(buf, o.Width, o.Height));
                }
                return new HelperSnapshot(header, images);
            }, "snapshot");
        }
        catch (Exception e)
        {
            _log($"helper unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>Region crops (output-local rectangle) from every frame the helper recorded around the trigger.</summary>
    public List<(int OffsetMs, FloatImage Crop)> GetRingCrops(string deviceName, int left, int top, int width, int height)
    {
        var result = new List<(int, FloatImage)>();
        try
        {
            Deadline(RingDeadline, async ct =>
            {
                using var pipe = await ConnectAsync(TimeSpan.FromMilliseconds(500), ct);
                await WriteLineAsync(pipe, JsonSerializer.Serialize(new { op = "ring", output = deviceName, left, top, width, height }), ct);
                using JsonDocument head = JsonDocument.Parse(await ReadLineAsync(pipe, ct));
                int count = head.RootElement.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 0;
                for (int i = 0; i < count; i++)
                {
                    using JsonDocument meta = JsonDocument.Parse(await ReadLineAsync(pipe, ct));
                    int offset = meta.RootElement.GetProperty("offsetMs").GetInt32();
                    int w = meta.RootElement.GetProperty("width").GetInt32(), h = meta.RootElement.GetProperty("height").GetInt32();
                    byte[] buf = await ReadExactAsync(pipe, w * h * 3 * 2, ct);
                    result.Add((offset, Half16Codec.Decode(buf, w, h)));
                }
                return true;
            }, "ring");
        }
        catch (Exception e)
        {
            _log($"helper ring unavailable: {e.Message}");
        }
        return result;
    }

    public void Consume() => Fire("{\"op\":\"consume\"}");

    public void ReportRegion(string inputPath, int left, int top, int width, int height)
        => Fire(JsonSerializer.Serialize(new { op = "last-region", input = inputPath, left, top, width, height }));

    private void Fire(string requestLine)
    {
        try
        {
            Deadline(NotifyDeadline, async ct =>
            {
                using var pipe = await ConnectAsync(TimeSpan.FromMilliseconds(300), ct);
                await WriteLineAsync(pipe, requestLine, ct);
                await ReadLineAsync(pipe, ct);
                return true;
            }, "notify");
        }
        catch (Exception e)
        {
            _log($"helper notify failed: {e.Message}");
        }
    }

    private T? Deadline<T>(TimeSpan limit, Func<CancellationToken, Task<T?>> work, string what)
    {
        using var cts = new CancellationTokenSource(limit);
        Task<T?> task = work(cts.Token);
        if (!task.Wait(limit))
        {
            cts.Cancel();
            _log($"helper {what} did not answer within {limit.TotalSeconds:F0} s; continuing without it");
            return default;
        }
        if (task.IsFaulted) throw task.Exception!.GetBaseException();
        return task.Result;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int length, CancellationToken ct)
    {
        var buf = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, length - read), ct);
            if (n <= 0) throw new EndOfStreamException("helper closed the pipe early");
            read += n;
        }
        return buf;
    }

    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(256);
        var one = new byte[1];
        while (buf.Count < 1 << 20)
        {
            int n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n <= 0 || one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await s.FlushAsync(ct);
    }
}
