using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Fires when ShareX shows its region-selection overlay (a window from ShareX.exe covering the whole virtual
/// desktop). Used to align captures that were started from the tray menu or the command line, where no hotkey
/// was pressed. Needs the frame history to be useful: by the time the window shows, ShareX has already captured.
/// </summary>
public sealed class RegionWindowWatcher : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutOfContext = 0x0000, WineventSkipOwnProcess = 0x0002;
    private const int ObjidWindow = 0;

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    private readonly WinEventProc _proc;
    private readonly HelperLog _log;
    private IntPtr _hook;
    private DateTime _lastFire;

    /// <summary>Raised on the hook thread when ShareX's overlay appears.</summary>
    public event Action? RegionWindowShown;
    public bool Installed => _hook != IntPtr.Zero;

    public RegionWindowWatcher(HelperLog log)
    {
        _log = log;
        _proc = Callback;
    }

    /// <summary>Must be called on a thread that pumps messages.</summary>
    public void Install()
    {
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _proc, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        if (_hook == IntPtr.Zero) _log.Warn("region window watcher: SetWinEventHook failed");
        else _log.Info("region window watcher armed");
    }

    private void Callback(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjidWindow || idChild != 0 || hwnd == IntPtr.Zero) return;
        try
        {
            if (!GetWindowRect(hwnd, out Rect r)) return;
            int vx = GetSystemMetrics(SmXVirtualScreen), vy = GetSystemMetrics(SmYVirtualScreen), vw = GetSystemMetrics(SmCxVirtualScreen), vh = GetSystemMetrics(SmCyVirtualScreen);
            // ShareX's overlay covers the whole virtual desktop (or a whole monitor in active-monitor mode); require at least a full monitor's worth.
            if (r.Right - r.Left < 640 || r.Bottom - r.Top < 480) return;
            bool coversDesktop = r.Left <= vx && r.Top <= vy && r.Right >= vx + vw && r.Bottom >= vy + vh;
            if (!coversDesktop) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            using Process p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals("ShareX", StringComparison.OrdinalIgnoreCase)) return;
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFire).TotalMilliseconds < 500) return;
            _lastFire = now;
            RegionWindowShown?.Invoke();
        }
        catch
        {
            // window vanished mid-check; ignore
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}
