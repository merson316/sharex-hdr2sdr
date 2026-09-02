using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;

namespace Hdr2Sdr.Windows.Helper;

public sealed record HelperSnapshot(SnapshotHeader Header, List<FloatImage> Images);

/// <summary>Client side of the helper's named pipe. Every failure is reported through the log and returns null.</summary>
public sealed class HelperClient
{
    public const string PipeName = "hdr2sdr-helper";
    private readonly Action<string> _log;

    public HelperClient(Action<string> log) => _log = log;

    public HelperSnapshot? TryGetSnapshot(TimeSpan connectTimeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect((int)connectTimeout.TotalMilliseconds);
            WriteLine(pipe, "{\"op\":\"get\"}");
            string status = ReadLine(pipe);
            using (JsonDocument doc = JsonDocument.Parse(status))
            {
                if (!doc.RootElement.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
                {
                    _log("helper has " + (doc.RootElement.TryGetProperty("reason", out JsonElement r) ? r.GetString() : "no snapshot"));
                    return null;
                }
            }
            SnapshotHeader header = SnapshotHeader.FromJson(ReadLine(pipe));
            var images = new List<FloatImage>(header.Outputs.Count);
            foreach (SnapshotOutput o in header.Outputs)
            {
                var buf = new byte[o.Width * o.Height * 3 * 2];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = pipe.Read(buf, read, buf.Length - read);
                    if (n <= 0) throw new EndOfStreamException("helper closed the pipe early");
                    read += n;
                }
                images.Add(Half16Codec.Decode(buf, o.Width, o.Height));
            }
            return new HelperSnapshot(header, images);
        }
        catch (TimeoutException)
        {
            _log("helper not running (pipe connect timed out)");
            return null;
        }
        catch (Exception e)
        {
            _log($"helper unavailable: {e.Message}");
            return null;
        }
    }

    public void Consume() => Fire("{\"op\":\"consume\"}");

    /// <summary>Region crops (output-local rectangle) from every frame the helper recorded after the hotkey.</summary>
    public List<(int OffsetMs, FloatImage Crop)> GetRingCrops(string deviceName, int left, int top, int width, int height)
    {
        var result = new List<(int, FloatImage)>();
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(200);
            WriteLine(pipe, JsonSerializer.Serialize(new { op = "ring", output = deviceName, left, top, width, height }));
            using JsonDocument head = JsonDocument.Parse(ReadLine(pipe));
            int count = head.RootElement.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 0;
            for (int i = 0; i < count; i++)
            {
                using JsonDocument meta = JsonDocument.Parse(ReadLine(pipe));
                int offset = meta.RootElement.GetProperty("offsetMs").GetInt32();
                int w = meta.RootElement.GetProperty("width").GetInt32(), h = meta.RootElement.GetProperty("height").GetInt32();
                var buf = new byte[w * h * 3 * 2];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = pipe.Read(buf, read, buf.Length - read);
                    if (n <= 0) throw new EndOfStreamException("helper closed the pipe early");
                    read += n;
                }
                result.Add((offset, Half16Codec.Decode(buf, w, h)));
            }
        }
        catch (Exception e)
        {
            _log($"helper ring unavailable: {e.Message}");
        }
        return result;
    }

    public void ReportRegion(string inputPath, int left, int top, int width, int height)
        => Fire(JsonSerializer.Serialize(new { op = "last-region", input = inputPath, left, top, width, height }));

    private void Fire(string requestLine)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(200);
            WriteLine(pipe, requestLine);
            ReadLine(pipe);
        }
        catch (Exception e)
        {
            _log($"helper notify failed: {e.Message}");
        }
    }

    private static string ReadLine(Stream s)
    {
        var buf = new List<byte>(256);
        int b;
        while ((b = s.ReadByte()) >= 0 && b != '\n' && buf.Count < 1 << 20) buf.Add((byte)b);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static void WriteLine(Stream s, string line)
    {
        s.Write(Encoding.UTF8.GetBytes(line + "\n"));
        s.Flush();
    }
}
