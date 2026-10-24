using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Interop.UIAutomationClient;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Font and color of the text just before the caret, so the ghost text can
/// match what the user is typing. The size is in physical pixels as UI
/// Automation reports it (without the app's zoom); a sample of the text
/// before the caret and its on-screen width tell the overlay the zoom.
public sealed record TextStyle(string? FontFamily = null, double? FontSizePx = null, Color? Color = null,
                               string? Sample = null, double? SampleWidthPx = null);

/// What the user is typing into right now, read through UI Automation.
public sealed record FocusContext(
    int ProcessId,
    /// Lowercase executable name ("notepad.exe"), used for the disabled-apps list.
    string AppId,
    string AppName,
    string? WindowTitle,
    string Role,
    /// Text before the caret (at most `FocusReader.MaxCharacters`), with "\n" line breaks.
    string TextBefore,
    /// Text after the caret up to the end of the line.
    string TextAfterOnLine,
    /// Caret rectangle in physical screen pixels.
    Rect? Caret,
    TextStyle Style,
    /// The text field's frame in physical screen pixels.
    Rect? ElementFrame,
    bool SingleLine);

public sealed record FocusResult(FocusContext? Context, string? Failure);

/// Reads the focused text field with UI Automation (COM IUIAutomation) on a
/// dedicated MTA thread, the Windows side of FocusReader.swift: text and caret
/// from TextPattern/TextPattern2, the caret rectangle from the bounding
/// rectangles of the character before the caret, with GetGUIThreadInfo as a
/// fallback for classic Win32 edit controls.
public sealed class FocusReader : IDisposable
{
    public const int MaxCharacters = 2000;

    private const int UIA_TextPatternId = 10014;
    private const int UIA_TextPattern2Id = 10024;
    private const int UIA_FontNameAttributeId = 40005;
    private const int UIA_FontSizeAttributeId = 40006;
    private const int UIA_ForegroundColorAttributeId = 40008;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_ComboBoxControlTypeId = 50003;

    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private IUIAutomation? automation;
    private readonly Dictionary<int, (string Id, string Name, bool? Elevated)> processes = [];
    private static readonly bool SelfElevated = IsElevated(Environment.ProcessId) == true;

    public FocusReader()
    {
        thread = new Thread(() =>
        {
            try
            {
                var uia = new CUIAutomation8();
                // Don't hang on an app that stops responding (the default is 20 s).
                if (uia is IUIAutomation2 uia2)
                {
                    uia2.ConnectionTimeout = 1500;
                    uia2.TransactionTimeout = 300;
                }
                automation = uia;
            }
            catch (Exception e)
            {
                AppStatus.Log($"UI Automation unavailable: {e.Message}");
            }
            foreach (var work in queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "OpenTypist UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Dispose() => queue.CompleteAdding();

    /// Reads the focused field on the UI Automation thread.
    public Task<FocusResult> ReadAsync()
    {
        var done = new TaskCompletionSource<FocusResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { done.SetResult(Read()); }
            catch (COMException e) { done.SetResult(new FocusResult(null, $"UI Automation error 0x{e.HResult:X8}")); }
            catch (Exception e) { done.SetResult(new FocusResult(null, $"UI Automation error: {e.Message}")); }
        });
        return done.Task;
    }

    private FocusResult Fail(string reason) => new(null, reason);

    private FocusResult Read()
    {
        if (automation is null) return Fail("UI Automation isn't available");
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return Fail("No active app");
        uint threadId = GetWindowThreadProcessId(hwnd, out int windowPid);
        if (windowPid == Environment.ProcessId) return Fail("OpenTypist's own window");
        var windowTitle = WindowTitle(hwnd);

        IUIAutomationElement? element;
        try { element = automation.GetFocusedElement(); }
        catch (COMException) { element = null; }
        var app = ProcessInfo(windowPid);
        if (element is null) return Fail($"{app.Name}: no focused text field found");

        // Apps hosted in another process (UWP in ApplicationFrameHost) report
        // their own process on the element.
        int pid = element.CurrentProcessId;
        if (pid == Environment.ProcessId) return Fail("OpenTypist's own window");
        if (pid != 0 && pid != windowPid) app = ProcessInfo(pid);
        else pid = windowPid;

        if (app.Elevated == true && !SelfElevated)
        {
            return Fail($"{app.Name}: runs as administrator, so Windows doesn't let OpenTypist type there");
        }

        int controlType = element.CurrentControlType;
        string role = RoleName(controlType);
        // Never read or complete password fields.
        if (element.CurrentIsPassword != 0) return Fail($"{app.Name}: password field, skipped");

        var pattern2 = element.GetCurrentPattern(UIA_TextPattern2Id) as IUIAutomationTextPattern2;
        var pattern = pattern2 as IUIAutomationTextPattern ?? element.GetCurrentPattern(UIA_TextPatternId) as IUIAutomationTextPattern;
        if (pattern is null) return Fail($"{app.Name}: focused element ({role}) doesn't report a caret");

        IUIAutomationTextRange? caret = null;
        var selection = pattern.GetSelection();
        if (selection is not null && selection.Length > 0)
        {
            var range = selection.GetElement(0);
            if (range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range,
                                       TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0)
            {
                return Fail($"{app.Name}: text is selected");
            }
            caret = range;
        }
        if (caret is null && pattern2 is not null)
        {
            caret = pattern2.GetCaretRange(out _);
        }
        if (caret is null) return Fail($"{app.Name}: focused element ({role}) doesn't report a caret");

        var beforeRange = caret.Clone();
        beforeRange.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -MaxCharacters);
        var before = beforeRange.GetText(-1) ?? "";
        var afterRange = caret.Clone();
        afterRange.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 200);
        var after = afterRange.GetText(-1) ?? "";

        before = TextUtil.NormalizeNewlines(before);
        after = TextUtil.NormalizeNewlines(after);
        int newline = after.IndexOf('\n');
        if (newline >= 0) after = after[..newline];
        // Some apps report a non-breaking space for the trailing space.
        before = before.Replace((char)0xA0, ' ');

        bool previousIsNewline = before.Length > 0 && before[^1] == '\n';
        var previousCharacter = caret.Clone();
        previousCharacter.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -1);

        var frame = ElementFrame(element);
        var previousRect = before.Length > 0 && !previousIsNewline ? LastRect(previousCharacter) : null;
        var caretRect = CaretRect(caret, previousRect, threadId);
        var style = previousRect is Rect p ? Style(previousCharacter, p) : new TextStyle();
        if (style.FontSizePx is not null && ZoomSample(caret, before) is (string sample, double width))
        {
            style = style with { Sample = sample, SampleWidthPx = width };
        }
        bool singleLine = controlType is UIA_EditControlTypeId or UIA_ComboBoxControlTypeId && !IsMultiLine(element);

        return new FocusResult(new FocusContext(pid, app.Id, app.Name, windowTitle, role, before, after, caretRect, style, frame, singleLine), null);
    }

    private static string RoleName(int controlType) => controlType switch
    {
        UIA_EditControlTypeId => "edit",
        UIA_DocumentControlTypeId => "document",
        UIA_ComboBoxControlTypeId => "combo box",
        50000 => "button",
        50026 => "group",
        50033 => "pane",
        50020 => "text",
        50025 => "custom",
        _ => $"control type {controlType}",
    };

    /// Win32 edit controls say whether they are multi-line in their style;
    /// for others, a field much taller than a line is multi-line.
    private static bool IsMultiLine(IUIAutomationElement element)
    {
        try
        {
            var rect = element.CurrentBoundingRectangle;
            return rect.bottom - rect.top > 60;
        }
        catch (COMException) { return false; }
    }

    private static Rect? ElementFrame(IUIAutomationElement element)
    {
        try
        {
            var r = element.CurrentBoundingRectangle;
            if (r.right <= r.left || r.bottom <= r.top) return null;
            return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }
        catch (COMException) { return null; }
    }

    private static Rect? LastRect(IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles() is not double[] values || values.Length < 4) return null;
        int i = values.Length - 4;
        var r = new Rect(values[i], values[i + 1], values[i + 2], values[i + 3]);
        return r.Height > 1 && (r.X != 0 || r.Y != 0) ? r : null;
    }

    private static Rect? FirstRect(IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles() is not double[] values || values.Length < 4) return null;
        var r = new Rect(values[0], values[1], values[2], values[3]);
        return r.Height > 1 && (r.X != 0 || r.Y != 0) ? r : null;
    }

    private static Rect? CaretRect(IUIAutomationTextRange caret, Rect? previousCharacter, uint threadId)
    {
        // Right edge of the character before the caret.
        if (previousCharacter is Rect p && p.Width < 200)
        {
            return new Rect(p.Right, p.Top, 1, p.Height);
        }
        // Some apps give the collapsed caret range itself a rectangle.
        if (FirstRect(caret) is Rect c)
        {
            return new Rect(c.Left, c.Top, 1, c.Height);
        }
        // Left edge of the character after the caret.
        var next = caret.Clone();
        next.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 1);
        if (FirstRect(next) is Rect n && n.Width < 200)
        {
            return new Rect(n.Left, n.Top, 1, n.Height);
        }
        // Classic Win32 controls: the system caret.
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref info) && info.hwndCaret != IntPtr.Zero && info.rcCaret.Height > 1)
        {
            var point = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            ClientToScreen(info.hwndCaret, ref point);
            return new Rect(point.X, point.Y, 1, info.rcCaret.Height);
        }
        return null;
    }

    /// Up to 16 characters before the caret on its line (without trailing
    /// spaces, which some apps give no width) and their on-screen width. One
    /// character alone is too coarse: browsers round each box to pixels.
    private static (string Sample, double Width)? ZoomSample(IUIAutomationTextRange caret, string before)
    {
        var line = before[(before.LastIndexOf('\n') + 1)..];
        var trimmed = line.TrimEnd(' ');
        int trailing = line.Length - trimmed.Length;
        var sample = TextUtil.Suffix(trimmed, 16);
        if (sample.Length < 3 || sample.Contains('\t')) return null;
        // Start first: moving the end of a collapsed range back drags the start along.
        var range = caret.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -(trailing + sample.Length));
        if (trailing > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, -trailing);
        // One rectangle: the sample is on one visual line.
        if (range.GetBoundingRectangles() is not double[] values || values.Length != 4 || values[2] < 4) return null;
        return (sample, values[2]);
    }

    private static TextStyle Style(IUIAutomationTextRange previousCharacter, Rect bounds)
    {
        string? family = null;
        double? sizePx = null;
        Color? color = null;
        try
        {
            if (previousCharacter.GetAttributeValue(UIA_FontNameAttributeId) is string name && name.Length > 0) family = name;
            if (previousCharacter.GetAttributeValue(UIA_FontSizeAttributeId) is double points && points >= 5 && points <= 96)
            {
                sizePx = points * 96.0 / 72.0 * ScaleAt((int)bounds.X, (int)bounds.Y);
            }
            if (previousCharacter.GetAttributeValue(UIA_ForegroundColorAttributeId) is int colorRef)
            {
                color = Color.FromRgb((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
            }
        }
        catch (COMException) { }
        return new TextStyle(family, sizePx, color);
    }

    /// "Notepad", "Microsoft Edge": the file description, which some apps
    /// set to their file name ("Notepad.exe").
    public static string DisplayName(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var version = FileVersionInfo.GetVersionInfo(exePath);
            var description = version.FileDescription?.Trim();
            if (string.IsNullOrEmpty(description)) description = version.ProductName?.Trim();
            if (!string.IsNullOrEmpty(description)) name = description;
        }
        catch (IOException) { }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private (string Id, string Name, bool? Elevated) ProcessInfo(int pid)
    {
        if (processes.TryGetValue(pid, out var cached)) return cached;
        var path = ProcessImagePath(pid);
        string id = path is null ? $"pid {pid}" : Path.GetFileName(path).ToLowerInvariant();
        string name = path is null ? id : DisplayName(path);
        var info = (id, name, path is null ? (bool?)true : IsElevated(pid));
        if (processes.Count > 200) processes.Clear();
        processes[pid] = info;
        return info;
    }
}
