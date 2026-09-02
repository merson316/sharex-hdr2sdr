using System.Diagnostics;
using System.Runtime.InteropServices;
using Hdr2Sdr.Core.Snapshot;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Low-level keyboard hook that fires when one of ShareX's capture hotkeys is pressed. It only compares
/// key-down events against the configured combinations; nothing is recorded or logged.
/// </summary>
public sealed class HotkeyWatcher : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100, WmSyskeydown = 0x0104;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLwin = 0x5B, VkRwin = 0x5C;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private readonly HookProc _proc;   // kept alive for the hook's lifetime
    private readonly HelperLog _log;
    private readonly string _configPath;
    private IntPtr _hook;
    private volatile List<HotkeyCombo> _combos = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastFire;

    /// <summary>Raised on the hook thread; handlers must return immediately.</summary>
    public event Action<HotkeyCombo>? Hotkey;
    public bool Installed => _hook != IntPtr.Zero;
    public int ComboCount => _combos.Count;
    public bool Paused { get; set; }

    public HotkeyWatcher(string shareXHotkeysConfigPath, HelperLog log)
    {
        _configPath = shareXHotkeysConfigPath;
        _log = log;
        _proc = Callback;
    }

    /// <summary>Must be called on a thread that pumps messages (the WinForms UI thread).</summary>
    public void Install()
    {
        Reload();
        string? dir = Path.GetDirectoryName(_configPath);
        if (dir != null && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) _log.Error($"keyboard hook failed: {Marshal.GetLastWin32Error()}");
        else _log.Info($"keyboard hook installed, {_combos.Count} ShareX capture hotkeys");
    }

    private void Reload()
    {
        try
        {
            if (!File.Exists(_configPath)) { _combos = new List<HotkeyCombo>(); _log.Warn($"ShareX hotkeys not found at {_configPath}"); return; }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    _combos = ShareXHotkeys.Parse(File.ReadAllText(_configPath));
                    _log.Info($"loaded {_combos.Count} capture hotkeys: {string.Join(", ", _combos.Select(c => c.Job))}");
                    return;
                }
                catch (IOException) { Thread.Sleep(100); }   // ShareX still writing
            }
        }
        catch (Exception e)
        {
            _log.Warn($"hotkey config reload failed: {e.Message}");
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Paused && ((int)wParam == WmKeydown || (int)wParam == WmSyskeydown))
        {
            int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
            List<HotkeyCombo> combos = _combos;
            if (combos.Count > 0 && combos.Any(c => c.VirtualKey == vk))
            {
                bool ctrl = Down(VkControl), shift = Down(VkShift), alt = Down(VkMenu), win = Down(VkLwin) || Down(VkRwin);
                foreach (HotkeyCombo c in combos)
                {
                    if (!c.Matches(vk, ctrl, shift, alt, win)) continue;
                    DateTime now = DateTime.UtcNow;
                    if ((now - _lastFire).TotalMilliseconds > 250)   // key repeat guard
                    {
                        _lastFire = now;
                        Hotkey?.Invoke(c);
                    }
                    break;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        _watcher?.Dispose();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
