using System.Diagnostics;

namespace Hdr2Sdr.App;

public sealed class Log : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly bool _verbose;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public Log(bool verbose)
    {
        _verbose = verbose;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hdr2sdr");
            Directory.CreateDirectory(dir);
            _file = new StreamWriter(Path.Combine(dir, "last.log"), append: false) { AutoFlush = true };
        }
        catch
        {
            _file = null;
        }
    }

    public void Info(string message) => Write("info", message, _verbose);
    public void Warn(string message) => Write("warn", message, true);
    public void Error(string message) => Write("error", message, true);

    private void Write(string level, string message, bool toConsole)
    {
        string line = $"[{_clock.ElapsedMilliseconds,6} ms] {level}: {message}";
        _file?.WriteLine(line);
        if (toConsole) Console.Error.WriteLine(line);
    }

    public void Dispose() => _file?.Dispose();
}
