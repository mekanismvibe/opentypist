using System.Text.Json.Serialization;
using static OpenTypist.Win32;

namespace OpenTypist;

/// A key plus modifiers, used to accept suggestions. The Windows side of
/// AcceptKeys.swift, with virtual-key codes.
public sealed record KeyCombo(int Vk, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false, string? Character = null)
{
    public static readonly KeyCombo Tab = new(VK_TAB);
    public static readonly KeyCombo ShiftTab = new(VK_TAB, Shift: true);
    public static readonly KeyCombo RightArrow = new(0x27);
    public static readonly KeyCombo AltRightArrow = new(0x27, Alt: true);

    public bool Matches(KeyEvent e) =>
        e.Vk == Vk && e.Shift == Shift && e.Win == Win
        // AltGr reports Ctrl+Alt; it never matches a combo without them.
        && (e.Ctrl && !e.AltGr) == Ctrl && (e.Alt && !e.AltGr) == Alt;

    /// Non-typing keys: short hint shown after the ghost text, and the name
    /// shown in Settings.
    private static readonly Dictionary<int, (string Hint, string Display)> Names = new()
    {
        [VK_TAB] = ("tab", "Tab"), [VK_RETURN] = ("enter", "Enter"), [VK_SPACE] = ("space", "Space"),
        [0x25] = ("←", "←"), [0x27] = ("→", "→"), [0x28] = ("↓", "↓"), [0x26] = ("↑", "↑"),
        [0x24] = ("home", "Home"), [0x23] = ("end", "End"), [0x21] = ("pgup", "Page Up"), [0x22] = ("pgdn", "Page Down"),
        [0x2D] = ("ins", "Insert"), [0xC0] = ("`", "`"), [VK_ESCAPE] = ("esc", "Esc"), [VK_BACK] = ("⌫", "Backspace"),
        [VK_DELETE] = ("del", "Delete"),
        [0x70] = ("F1", "F1"), [0x71] = ("F2", "F2"), [0x72] = ("F3", "F3"), [0x73] = ("F4", "F4"),
        [0x74] = ("F5", "F5"), [0x75] = ("F6", "F6"), [0x76] = ("F7", "F7"), [0x77] = ("F8", "F8"),
        [0x78] = ("F9", "F9"), [0x79] = ("F10", "F10"), [0x7A] = ("F11", "F11"), [0x7B] = ("F12", "F12"),
    };

    private string ModifierPrefix => (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + (Win ? "Win+" : "");

    /// "Shift+Tab", "Alt+→", "Ctrl+J".
    [JsonIgnore]
    public string DisplayName => ModifierPrefix + (Names.TryGetValue(Vk, out var n) ? n.Display : Character ?? $"Key {Vk}");

    /// Short label for the hint after the ghost text: "tab", "→", "alt+j".
    [JsonIgnore]
    public string HintLabel => ModifierPrefix.ToLowerInvariant() + (Names.TryGetValue(Vk, out var n) ? n.Hint : Character?.ToLowerInvariant() ?? "?");

    /// Keys that would get in the way of normal typing are refused: letters,
    /// digits, Space, Backspace, Delete and Esc (which already dismisses)
    /// without Ctrl, Alt or Win.
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (Vk == VK_ESCAPE) return "Esc already dismisses suggestions.";
            if (Vk is VK_BACK or VK_DELETE) return "Delete keys can't accept suggestions.";
            bool hasModifier = Ctrl || Alt || Win;
            if (!hasModifier && (!Names.ContainsKey(Vk) || Vk == VK_SPACE)) return "Pick a key you don't type with, or add Ctrl, Alt or Win.";
            return null;
        }
    }
}

/// The two accept keys for an app (or everywhere).
public sealed record AcceptKeys(KeyCombo Word, KeyCombo All)
{
    public static readonly AcceptKeys Standard = new(KeyCombo.Tab, KeyCombo.ShiftTab);
}
