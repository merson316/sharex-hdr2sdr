using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Hdr2Sdr.Helper;

/// <summary>Named pipe "hdr2sdr": lets `hdr2sdr.exe --capture &lt;Job&gt;` and status queries reach the running instance.</summary>
public sealed class PipeServer : IDisposable
{
    public const string PipeName = "hdr2sdr";
    private readonly Func<string> _status;
    private readonly Action<string> _startCapture;
    private readonly HelperLog _log;
    private readonly CancellationTokenSource _cts = new();

    public PipeServer(Func<string> status, Action<string> startCapture, HelperLog log)
    {
        _status = status;
        _startCapture = startCapture;
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
                    case "capture":
                        _startCapture(doc.RootElement.TryGetProperty("job", out JsonElement j) ? j.GetString() ?? "" : "");
                        await WriteLine(pipe, "{\"ok\":true}");
                        break;
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
