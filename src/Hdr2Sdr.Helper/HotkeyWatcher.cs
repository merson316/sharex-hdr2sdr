using System.Runtime.InteropServices;
using Hdr2Sdr.Core.ShareX;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Low-level keyboard hook that fires when one of ShareX's capture hotkeys is pressed. It only compares
/// key-down events against the configured combinations; nothing is recorded or logged.
/// The hook lives on its own message-loop thread so UI work can never stall it (Windows silently removes
/// a low-level hook whose thread stops answering), and it is re-armed every few minutes as a safety net.
/// </summary>
public sealed class HotkeyWatcher : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100, WmSyskeydown = 0x0104, WmQuit = 0x0012, WmUser = 0x0400;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLwin = 0x5B, VkRwin = 0x5C;
    private const int RearmMessage = WmUser + 1;
    private static readonly TimeSpan RearmInterval = TimeSpan.FromMinutes(5);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private readonly HookProc _proc;   // kept alive for the hook's lifetime
    private readonly HelperLog _log;
    private readonly string _configPath;
    private IntPtr _hook;
    private volatile List<HotkeyCombo> _combos = new();
    private FileSystemWatcher? _watcher;
    private Thread? _thread;
    private uint _threadId;
    private System.Threading.Timer? _rearm;
    private DateTime _lastFire;
    private volatile bool _installed;

    /// <summary>Raised on the hook thread; handlers must return immediately.</summary>
    public event Action<HotkeyCombo>? Hotkey;
    /// <summary>When true, matching key-downs are swallowed (ShareX does not see them) so they can be re-injected later.</summary>
    public volatile bool Swallow;
    private const uint LlkhfInjected = 0x10;
    public bool Installed => _installed;
    public int ComboCount => _combos.Count;
    public bool Paused { get; set; }
    /// <summary>Extra hooks that need this thread's message loop (installed from the hook thread).</summary>
    public Action? OnHookThreadReady { get; set; }

    public HotkeyWatcher(string shareXHotkeysConfigPath, HelperLog log)
    {
        _configPath = shareXHotkeysConfigPath;
        _log = log;
        _proc = Callback;
    }

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
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "keyboard hook" };
        _thread.Start();
        ready.Wait(2000);
        _rearm = new System.Threading.Timer(_ => { if (_threadId != 0) PostThreadMessageW(_threadId, RearmMessage, IntPtr.Zero, IntPtr.Zero); }, null, RearmInterval, RearmInterval);
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        Arm();
        OnHookThreadReady?.Invoke();
        ready.Set();
        while (GetMessageW(out Msg msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == RearmMessage) Arm();
            if (msg.message == WmQuit) break;
        }
        Disarm();
    }

    private void Arm()
    {
        Disarm();
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        _installed = _hook != IntPtr.Zero;
        if (!_installed) _log.Error($"keyboard hook failed: {Marshal.GetLastWin32Error()}");
        else _log.Info($"keyboard hook armed, {_combos.Count} ShareX capture hotkeys");
    }

    private void Disarm()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _installed = false;
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
            uint flags = (uint)Marshal.ReadInt32(lParam, 8);
            bool injected = (flags & LlkhfInjected) != 0;   // our own re-injection: let it through untouched
            List<HotkeyCombo> combos = _combos;
            if (!injected && combos.Count > 0 && combos.Any(c => c.VirtualKey == vk))
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
                    if (Swallow) return (IntPtr)1;
                    break;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        _rearm?.Dispose();
        _watcher?.Dispose();
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
