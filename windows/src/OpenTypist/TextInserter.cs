using System.Runtime.InteropServices;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Types text into the focused app with `SendInput` Unicode events, marked
/// with `Marker` in dwExtraInfo so our own hook ignores them. The Windows side
/// of `TextInserter` in KeyboardMonitor.swift.
public static class TextInserter
{
    public static readonly UIntPtr Marker = 0x0F7E7157;

    /// Line breaks are sent as Shift+Enter, which adds a new line in chat
    /// apps (where a plain Enter would send the message) as well as in
    /// ordinary text fields.
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                inputs.Add(Key(VK_SHIFT, false));
                inputs.Add(Key(VK_RETURN, false));
                inputs.Add(Key(VK_RETURN, true));
                inputs.Add(Key(VK_SHIFT, true));
            }
            foreach (char c in lines[i])
            {
                inputs.Add(Unicode(c, false));
                inputs.Add(Unicode(c, true));
            }
        }
        Send(inputs);
    }

    /// Presses Backspace `count` times.
    public static void DeleteBackward(int count)
    {
        var inputs = new List<INPUT>();
        for (int i = 0; i < count; i++)
        {
            inputs.Add(Key(VK_BACK, false));
            inputs.Add(Key(VK_BACK, true));
        }
        Send(inputs);
    }

    /// Presses and releases one virtual key (used to replay a swallowed key).
    public static void Press(int vk)
    {
        Send([Key(vk, false), Key(vk, true)]);
    }

    private static void Send(List<INPUT> inputs)
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
            ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = Marker },
        },
    };

    private static INPUT Key(int vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker },
        },
    };
}
