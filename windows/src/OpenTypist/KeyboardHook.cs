using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static OpenTypist.Win32;

namespace OpenTypist;

/// A key press seen by the hook, with the modifiers held and the text it
/// types. `IsDeadKey`: an accent key that combines with the next key.
public sealed record KeyEvent(int Vk, uint ScanCode, bool Shift, bool Ctrl, bool Alt, bool Win, bool AltGr, string Text, bool IsDeadKey = false)
{
    public static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT
        or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL or VK_NUMLOCK or VK_SCROLL;

    /// A shortcut rather than typing: Ctrl or Win held (AltGr is typing).
    public bool IsShortcut => (Ctrl && !AltGr) || Win;
}

/// Global low-level keyboard and mouse hooks (`WH_KEYBOARD_LL`,
/// `WH_MOUSE_LL`) on their own thread with a message loop, the Windows side
/// of KeyboardMonitor.swift. Handlers run on that thread and must return
/// quickly: Windows silently removes a hook that is too slow, so no UI
/// Automation or model work happens here.
public sealed class KeyboardHook : IDisposable
{
    /// Returns true to swallow the key.
    public Func<KeyEvent, bool>? OnKeyDown;
    public Action<KeyEvent>? OnKeyUp;
    public Action? OnMouseDown;

    private Thread? thread;
    private uint threadId;
    private IntPtr keyboardHook, mouseHook;
    // Keep the delegates alive while the hooks are installed.
    private LowLevelProc? keyboardProc, mouseProc;
    /// Keys swallowed on key-down whose key-up must be swallowed too.
    private readonly HashSet<int> swallowed = [];

    public bool IsRunning => keyboardHook != IntPtr.Zero;

    private static readonly int[] TrackedModifiers = [VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN];
    /// Which modifiers the user holds, from the keys the hook saw (our own
    /// injected releases don't change it, unlike GetAsyncKeyState).
    private static readonly bool[] Held = new bool[256];

    public static List<int> HeldModifiers() => TrackedModifiers.Where(vk => Volatile.Read(ref Held[vk])).ToList();

    public bool Start()
    {
        if (thread is not null) return IsRunning;
        using var ready = new ManualResetEventSlim();
        foreach (var vk in TrackedModifiers) Held[vk] = IsKeyDown(vk);
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            keyboardProc = KeyboardCallback;
            mouseProc = MouseCallback;
            var module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "OpenTypist keyboard hook", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
        return IsRunning;
    }

    public void Dispose()
    {
        if (thread is null) return;
        PostThreadMessage(threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
        thread = null;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore the keystrokes we synthesize ourselves.
            if (info.dwExtraInfo != TextInserter.Marker)
            {
                int message = (int)wParam;
                int vk = (int)info.vkCode;
                uint late = unchecked((uint)Environment.TickCount - info.time);
                if (late > 100 && late < 60_000) AppStatus.Log($"keyboard hook: key {vk:X2} arrived {late} ms late");
                if (vk < 256 && Array.IndexOf(TrackedModifiers, vk) >= 0)
                {
                    Volatile.Write(ref Held[vk], message is WM_KEYDOWN or WM_SYSKEYDOWN);
                }
                try
                {
                    if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        if (!KeyEvent.IsModifier(vk))
                        {
                            var e = Describe(vk, info.scanCode);
                            long started = Stopwatch.GetTimestamp();
                            bool used = OnKeyDown?.Invoke(e) == true;
                            // Windows passes keys on (and can drop the hook) when this is slow.
                            var took = Stopwatch.GetElapsedTime(started);
                            if (took.TotalMilliseconds > 25) AppStatus.Log($"keyboard hook: {took.TotalMilliseconds:0} ms for key {vk:X2}");
                            if (used)
                            {
                                swallowed.Add(vk);
                                return 1;
                            }
                            // A correction is still waiting to be typed: this key
                            // goes after it, not into the middle of it.
                            if (InputQueue.IsBusy)
                            {
                                AppStatus.Log($"keyboard hook: key {vk:X2} held back until our keys are out");
                                swallowed.Add(vk);
                                var replay = new InputBatch();
                                if (e.Text.Length == 1 && !e.IsShortcut) replay.Replay(e.Text[0]);
                                else replay.Press(vk);
                                InputQueue.Enqueue(replay);
                                return 1;
                            }
                        }
                    }
                    else if (message is WM_KEYUP or WM_SYSKEYUP)
                    {
                        if (swallowed.Remove(vk)) return 1;
                        // Alt or Win let go while our keys are still going out
                        // (the menu mask after an Alt+→ accept): after them.
                        if (vk is VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN && InputQueue.IsBusy)
                        {
                            InputQueue.Enqueue(new InputBatch().Release(vk));
                            return 1;
                        }
                        if (!KeyEvent.IsModifier(vk)) OnKeyUp?.Invoke(Describe(vk, info.scanCode, typed: false));
                    }
                }
                catch (Exception ex)
                {
                    AppStatus.Log($"keyboard hook: {ex.Message}");
                }
            }
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            try { OnMouseDown?.Invoke(); } catch (Exception ex) { AppStatus.Log($"mouse hook: {ex.Message}"); }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private readonly byte[] keyState = new byte[256];
    private readonly StringBuilder chars = new(8);

    private KeyEvent Describe(int vk, uint scanCode, bool typed = true)
    {
        // Modifiers as this hook saw them: GetAsyncKeyState can lag behind
        // the events a low-level hook gets.
        bool shift = Held[VK_LSHIFT] || Held[VK_RSHIFT], ctrl = Held[VK_LCONTROL] || Held[VK_RCONTROL];
        bool alt = Held[VK_LMENU] || Held[VK_RMENU], win = Held[VK_LWIN] || Held[VK_RWIN];
        // AltGr arrives as left Ctrl + right Alt.
        bool altGr = Held[VK_RMENU] && Held[VK_LCONTROL];
        string text = "";
        bool deadKey = false;
        if (typed && vk == VK_PACKET)
        {
            // Unicode input from on-screen keyboards, remote desktops and
            // other apps' SendInput: the character is in the scan code.
            text = ((char)scanCode).ToString();
        }
        else if (typed && !(ctrl && !altGr) && !win)
        {
            Array.Clear(keyState);
            if (shift) keyState[VK_SHIFT] = 0x80;
            if (altGr) { keyState[VK_CONTROL] = 0x80; keyState[VK_MENU] = 0x80; }
            if ((GetKeyState(VK_CAPITAL) & 1) != 0) keyState[VK_CAPITAL] = 0x01;
            var hwnd = GetForegroundWindow();
            var layout = GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));
            chars.Clear();
            // Flag 4: don't change the keyboard state (keeps dead keys working for the app).
            int n = ToUnicodeEx((uint)vk, scanCode, keyState, chars, chars.Capacity, 4, layout);
            if (n > 0) text = chars.ToString(0, Math.Min(n, chars.Length));
            deadKey = n < 0;
            // Control characters (Tab, Enter, Backspace) aren't typed text here.
            if (text.Length > 0 && char.IsControl(text[0])) text = "";
        }
        return new KeyEvent(vk, scanCode, shift, ctrl, alt, win, altGr, text, deadKey);
    }
}
