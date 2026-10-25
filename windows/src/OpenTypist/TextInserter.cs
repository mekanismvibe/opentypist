using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Types text into the focused app with `SendInput`, marked with `Marker` in
/// dwExtraInfo so our own hook ignores it. The Windows side of `TextInserter`
/// in KeyboardMonitor.swift.
public static class TextInserter
{
    public static readonly UIntPtr Marker = 0x0F7E7157;

    /// Line breaks are sent as Shift+Enter, which adds a new line in chat
    /// apps (where a plain Enter would send the message) as well as in
    /// ordinary text fields. Queued like the hook's edits, so keys the user
    /// types meanwhile come after it.
    public static void Type(string text) => InputQueue.Enqueue(new InputBatch().Text(text));

    /// Presses and releases one virtual key (used to replay a swallowed key).
    public static void Press(int vk) => InputQueue.Enqueue(new InputBatch().Press(vk));
}

/// Keystrokes sent together, so nothing the user types can land in the
/// middle of a correction.
///
/// Apps read injected keys late when they are busy (Notepad redrawing after
/// Backspace), which breaks the two usual ways of typing text: a queue of
/// Unicode (`VK_PACKET`) events all turns into the last character, and
/// shifted keys see Shift already released. So characters the keyboard
/// layout types without Shift go out as its own keys (as robust as real
/// typing), and the others as Unicode events spaced out so they never queue
/// up. Words go out one at a time with a short pause: Notepad's spell check
/// otherwise drops what arrives right after a finished word (measured: 10 ms
/// lost text in 2 of 4 runs, 25 ms in none). Modifiers the user holds
/// (Shift+Tab, Alt+→) are released first so they don't change what is typed.
public sealed class InputBatch
{
    private enum Kind { Backspace, Text, Replay, Press, Release }

    private readonly List<(Kind Kind, string Text, int Vk)> items = [];

    public InputBatch Backspace(int count)
    {
        for (int i = 0; i < count; i++) items.Add((Kind.Backspace, "", VK_BACK));
        return this;
    }

    public InputBatch Text(string text)
    {
        if (text.Length > 0) items.Add((Kind.Text, text.Replace("\r\n", "\n"), 0));
        return this;
    }

    /// A key the hook swallowed, replayed after our edits: Enter and Tab as
    /// those keys (with Shift if the user held it), anything else as the
    /// character it typed.
    public InputBatch Replay(char ending)
    {
        items.Add((Kind.Replay, ending.ToString(), 0));
        return this;
    }

    public InputBatch Press(int vk)
    {
        items.Add((Kind.Press, "", vk));
        return this;
    }

    /// A key-up the hook held back (Alt or Win, until the menu mask is out).
    public InputBatch Release(int vk)
    {
        items.Add((Kind.Release, "", vk));
        return this;
    }

    /// An unassigned key: pressed while Alt or Win is down, it stops their
    /// release from opening the app's menu or the Start menu (AutoHotkey's
    /// "menu mask"). Needed when we swallow the key of an Alt+→ accept.
    public InputBatch MenuMask() => Press(MaskKey);

    public bool IsEmpty => items.Count == 0;

    /// For edits decided in the keyboard hook: `InputQueue` sends them right
    /// after the hook callback has returned.
    public void SendFromHook() => InputQueue.Enqueue(this);

    public void Send()
    {
        if (items.Count == 0) return;
        // Replaying a key the user pressed (Ctrl+V, an arrow): with the
        // modifiers they hold, untouched.
        if (items.All(i => i.Kind is Kind.Press or Kind.Release))
        {
            SendNow(items.SelectMany(i => i.Kind == Kind.Release ? [Key(i.Vk, true)] : new[] { Key(i.Vk, false), Key(i.Vk, true) }).ToList());
            return;
        }
        var inputs = new List<INPUT>();
        var hwnd = GetForegroundWindow();
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));
        // Chinese, Japanese and Korean layouts send keys through the IME.
        int language = (int)((long)layout & 0x3FF);
        bool unicodeOnly = language is 0x04 or 0x11 or 0x12;
        bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;

        // Let go of the modifiers the user holds. Releasing Alt or Win on its
        // own opens menus, so press an unassigned key first, as AutoHotkey does.
        var held = KeyboardHook.HeldModifiers();
        if (held.Any(vk => vk is VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN)) { inputs.Add(Key(MaskKey, false)); inputs.Add(Key(MaskKey, true)); }
        foreach (var vk in held) inputs.Add(Key(vk, true));
        bool userShift = held.Contains(VK_LSHIFT) || held.Contains(VK_RSHIFT);
        bool shift = false;

        void SetShift(bool down)
        {
            if (shift == down) return;
            if (!down)
            {
                // Apps read Shift when they get to the key: keep it down until
                // they have (Shift+Enter must not become Enter, which sends).
                SendNow(inputs);
                inputs.Clear();
                Thread.Sleep(ShiftHoldMs);
            }
            inputs.Add(Key(VK_LSHIFT, !down));
            shift = down;
        }

        void PressKey(int vk)
        {
            inputs.Add(Key(vk, false));
            inputs.Add(Key(vk, true));
        }

        bool sentUnicode = false;

        void TypeChar(char c)
        {
            short scan = unicodeOnly ? (short)-1 : VkKeyScanEx(c, layout);
            int vk = scan & 0xFF;
            bool needsModifiers = (scan & 0x0700) != 0;
            // Caps Lock flips letters.
            if (capsLock && char.IsLetter(c)) needsModifiers = !needsModifiers;
            bool dead = scan != -1 && (MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_CHAR, layout) & 0x80000000) != 0;
            if (scan != -1 && !needsModifiers && !dead && !char.IsSurrogate(c))
            {
                PressKey(vk);
                return;
            }
            // A Unicode event, after the app has taken the previous one.
            if (sentUnicode && !char.IsLowSurrogate(c))
            {
                SendNow(inputs);
                inputs.Clear();
                Thread.Sleep(UnicodeSpacingMs);
            }
            inputs.Add(Unicode(c, false));
            inputs.Add(Unicode(c, true));
            sentUnicode = true;
        }

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case Kind.Backspace:
                    SetShift(false);
                    PressKey(VK_BACK);
                    break;
                case Kind.Press:
                    SetShift(false);
                    PressKey(item.Vk);
                    break;
                case Kind.Replay:
                    char ending = item.Text[0];
                    if (ending is '\n' or '\t')
                    {
                        // Shift+Enter stays a new line in chat apps.
                        SetShift(userShift);
                        PressKey(ending == '\n' ? VK_RETURN : VK_TAB);
                    }
                    else TypeChar(ending);
                    break;
                case Kind.Text:
                    var lines = item.Text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0)
                        {
                            SetShift(true);
                            PressKey(VK_RETURN);
                            SetShift(false);
                        }
                        for (int j = 0; j < lines[i].Length; j++)
                        {
                            TypeChar(lines[i][j]);
                            // A word or sentence ended and more follows: let the app catch up.
                            char typed = lines[i][j];
                            if ((char.IsWhiteSpace(typed) || AutocorrectRules.IsEndCharacter(typed)) && j + 1 < lines[i].Length)
                            {
                                SendNow(inputs);
                                inputs.Clear();
                                Thread.Sleep(WordPauseMs);
                            }
                        }
                    }
                    break;
            }
        }
        SetShift(false);
        // Modifiers we released stay released: pressing them again for the
        // user risks leaving one stuck down, which is far worse than having
        // to press Shift again.
        SendNow(inputs);
    }

    /// An unassigned virtual key, pressed before releasing Alt or Win.
    private const int MaskKey = 0xE8;
    private const int UnicodeSpacingMs = 15;
    private const int ShiftHoldMs = 40;
    public const int WordPauseMs = 30;

    /// Whether the batch ends a word, so whatever is sent next should wait.
    public bool EndsWord => items.Count > 0 && items[^1].Kind is Kind.Text or Kind.Replay
                            && items[^1].Text.Length > 0 && (char.IsWhiteSpace(items[^1].Text[^1]) || AutocorrectRules.IsEndCharacter(items[^1].Text[^1]));

    private static void SendNow(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent != array.Length)
        {
            // UIPI blocks input to windows running as administrator.
            AppStatus.Log($"SendInput sent {sent} of {array.Length} events (error {Marshal.GetLastWin32Error()})");
        }
    }

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = TextInserter.Marker },
        },
    };

    private static INPUT Key(int vk, bool up)
    {
        // Right-hand modifiers and navigation keys are extended keys.
        bool extended = vk is VK_RMENU or VK_RCONTROL or VK_LWIN or VK_RWIN or VK_DELETE or (>= 0x21 and <= 0x28) or 0x2D;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKey((uint)vk, 0),
                    dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                    dwExtraInfo = TextInserter.Marker,
                },
            },
        };
    }
}

/// Sends edits decided in the keyboard hook, in order, as soon as the hook
/// callback has returned. While one is waiting, the hook holds back the
/// user's next keys and replays them here after it, so nothing typed lands
/// in the middle of a correction.
public static class InputQueue
{
    private static readonly BlockingCollection<InputBatch> Queue = new();
    private static int pending;

    static InputQueue()
    {
        new Thread(() =>
        {
            foreach (var batch in Queue.GetConsumingEnumerable())
            {
                try
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    batch.Send();
                    AppStatus.Log($"input: batch sent in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
                    // Keys held back behind a correction start a new word.
                    if (batch.EndsWord && Queue.Count > 0) Thread.Sleep(InputBatch.WordPauseMs);
                }
                catch (Exception e) { AppStatus.Log($"input: {e.Message}"); }
                Interlocked.Decrement(ref pending);
            }
        })
        { IsBackground = true, Name = "OpenTypist input", Priority = ThreadPriority.Highest }.Start();
    }

    public static bool IsBusy => Volatile.Read(ref pending) > 0;

    public static void Enqueue(InputBatch batch)
    {
        if (batch.IsEmpty) return;
        Interlocked.Increment(ref pending);
        Queue.Add(batch);
    }
}
