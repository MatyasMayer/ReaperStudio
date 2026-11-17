using System.Runtime.InteropServices;

namespace ReaperStudio.Core.Input;

/// <summary>
/// Low-level keyboard hook used only to drive reactive lighting. It reports virtual-key
/// codes of key-down events and never records or stores what is typed.
/// </summary>
/// <remarks>
/// Windows calls a low-level hook synchronously for every key press system-wide, so the hook
/// lives on its own thread with its own message loop. That way a busy UI thread can never
/// delay typing in other apps.
/// </remarks>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmSysKeyDown = 0x104, WmQuit = 0x12;
    private const int VkReturn = 0x0D, VkNumpadEnterLayout = 0xFD, LlkhfExtended = 0x01, LlkhfInjected = 0x10;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public int VkCode, ScanCode, Flags, Time; public nint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);

    private readonly HookProc _proc; // keep delegate alive for the hook's lifetime
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly HashSet<int> _down = [];
    private nint _hook;
    private uint _threadId;

    /// <summary>Raised on the hook thread on key-down (not on auto-repeat) with the layout VK code. Keep handlers fast.</summary>
    public event Action<int>? KeyDown;

    public KeyboardHook()
    {
        _proc = Callback;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ReaperStudio keyboard hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        _started.Wait(2000);
    }

    public bool IsInstalled => _hook != 0;

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        _started.Set();
        while (GetMessageW(out _, 0, 0, 0) > 0) { }
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int vk = info.VkCode;
            if (vk == VkReturn && (info.Flags & LlkhfExtended) != 0) vk = VkNumpadEnterLayout;
            if (wParam is WmKeyDown or WmSysKeyDown)
            {
                if (_down.Add(vk) && (info.Flags & LlkhfInjected) == 0)
                {
                    try { KeyDown?.Invoke(vk); }
                    catch (Exception) { /* never let a lighting bug break the user's typing */ }
                }
            }
            else _down.Remove(vk);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(1000);
        _started.Dispose();
    }
}
