namespace Hdr2Sdr.Helper;

/// <summary>Append-only log at %LOCALAPPDATA%\hdr2sdr\helper.log, truncated when it passes 1 MB.</summary>
public sealed class HelperLog
{
    private readonly object _lock = new();
    public string Path { get; }

    public HelperLog()
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hdr2sdr");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "helper.log");
    }

    public void Info(string message) => Write("info", message);
    public void Warn(string message) => Write("warn", message);
    public void Error(string message) => Write("error", message);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.WriteAllText(Path, "");
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level}: {message}{Environment.NewLine}");
            }
            catch
            {
                // logging must never take the helper down
            }
        }
    }
}
