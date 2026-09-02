using System.Diagnostics;

namespace Hdr2Sdr.Helper;

/// <summary>Registers the helper as a Task Scheduler logon task for the current user (no admin rights).</summary>
public static class StartupTask
{
    public const string TaskName = "hdr2sdr-helper";

    public static bool IsInstalled() => Run("/Query", "/TN", TaskName).ExitCode == 0;

    public static string Install(string exePath)
    {
        var r = Run("/Create", "/F", "/SC", "ONLOGON", "/RL", "LIMITED", "/TN", TaskName, "/TR", $"\"{exePath}\"");
        return r.ExitCode == 0 ? "" : r.Output;
    }

    public static string Remove()
    {
        var r = Run("/Delete", "/F", "/TN", TaskName);
        return r.ExitCode == 0 ? "" : r.Output;
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}
