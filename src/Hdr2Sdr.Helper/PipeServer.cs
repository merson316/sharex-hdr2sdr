using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hdr2Sdr.Core.Snapshot;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Named pipe "hdr2sdr-helper". One JSON request line per connection; replies with one JSON line, and for
/// "get" the per-output half-float pixel blocks follow the header in list order.
/// </summary>
public sealed class PipeServer : IDisposable
{
    public const string PipeName = "hdr2sdr-helper";
    private readonly SnapshotStore _store;
    private readonly Func<IReadOnlyList<CaptureLoop>> _loops;
    private readonly Func<string> _status;
    private readonly HelperLog _log;
    private readonly CancellationTokenSource _cts = new();

    public PipeServer(SnapshotStore store, Func<IReadOnlyList<CaptureLoop>> loops, Func<string> status, HelperLog log)
    {
        _store = store;
        _loops = loops;
        _status = status;
        _log = log;
    }

    public void Start() => Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => Handle(server));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log.Warn($"pipe accept: {e.Message}");
                await Task.Delay(500);
            }
        }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                string request = await ReadLine(pipe);
                using JsonDocument doc = JsonDocument.Parse(request);
                string op = doc.RootElement.TryGetProperty("op", out JsonElement o) ? o.GetString() ?? "" : "";
                switch (op)
                {
                    case "get":
                    {
                        Snapshot? s = _store.Current;
                        if (s == null) { await WriteLine(pipe, "{\"ok\":false,\"reason\":\"no snapshot\"}"); break; }
                        await WriteLine(pipe, "{\"ok\":true}");
                        await WriteLine(pipe, s.Header.ToJson());
                        for (int i = 0; i < s.Images.Count; i++)
                        {
                            byte[] halves = Half16Codec.Encode(s.Images[i]);
                            await pipe.WriteAsync(halves);
                        }
                        await pipe.FlushAsync();
                        break;
                    }
                    case "consume":
                        _store.Consume(_loops());
                        await WriteLine(pipe, "{\"ok\":true}");
                        break;
                    case "ring":
                    {
                        JsonElement r = doc.RootElement;
                        string device = r.GetProperty("output").GetString() ?? "";
                        CaptureLoop? loop = _loops().FirstOrDefault(l => l.Output.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase));
                        var crops = loop?.RingCrops(r.GetProperty("left").GetInt32(), r.GetProperty("top").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32())
                                    ?? new List<(int, Core.Imaging.FloatImage)>();
                        await WriteLine(pipe, JsonSerializer.Serialize(new { ok = true, count = crops.Count }));
                        foreach (var (offset, crop) in crops)
                        {
                            await WriteLine(pipe, JsonSerializer.Serialize(new { offsetMs = offset, width = crop.Width, height = crop.Height }));
                            await pipe.WriteAsync(Half16Codec.Encode(crop));
                        }
                        await pipe.FlushAsync();
                        break;
                    }
                    case "last-region":
                    {
                        JsonElement r = doc.RootElement;
                        _store.LastRegion = new LastRegion(r.GetProperty("input").GetString() ?? "", r.GetProperty("left").GetInt32(), r.GetProperty("top").GetInt32(),
                            r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), DateTime.UtcNow);
                        await WriteLine(pipe, "{\"ok\":true}");
                        break;
                    }
                    case "status":
                        await WriteLine(pipe, JsonSerializer.Serialize(new { ok = true, status = _status() }));
                        break;
                    default:
                        await WriteLine(pipe, "{\"ok\":false,\"reason\":\"unknown op\"}");
                        break;
                }
            }
            catch (Exception e)
            {
                _log.Warn($"pipe request failed: {e.Message}");
            }
        }
    }

    private static async Task<string> ReadLine(Stream s)
    {
        var buf = new List<byte>(256);
        var one = new byte[1];
        while (buf.Count < 64 * 1024)
        {
            int n = await s.ReadAsync(one);
            if (n == 0 || one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static async Task WriteLine(Stream s, string line)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await s.FlushAsync();
    }

    public void Dispose() => _cts.Cancel();
}
