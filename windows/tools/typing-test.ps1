<#
.SYNOPSIS
End-to-end check of the running app: types into Notepad or a Microsoft Edge
text area like a person would (real virtual-key presses, ~70 ms apart),
then accepts with Tab and Shift+Tab, and reports what the field contains, the
app's "Last check" log lines and a screenshot of the ghost text.

.DESCRIPTION
Don't touch the keyboard or mouse while it runs: keys go to the focused
window. It stops as soon as another window takes the focus.

Start OpenTypist with $env:OPENTYPIST_LOG = '1' first (this script does it if
OpenTypist isn't running). Edge runs with a throwaway profile in -Out.
#>
param(
    # edge-password: a password field, where no suggestion may appear.
    [ValidateSet('notepad', 'edge', 'edge-password')] [string]$Target = 'notepad',
    # suggest: accept with Tab / Shift+Tab. autocorrect: list fix, Backspace
    # undo, model fix, snippet (needs an @@ snippet). midline: the card.
    # wrap: a narrow Edge text area. appkeys: Right / Alt+Right (needs
    # per-app keys for the target). learn: names, greetings and sign-offs
    # typed three times (check Settings > Personal afterwards). screen: an
    # Edge page with a message above the text area (needs "Use text visible
    # on screen").
    [ValidateSet('suggest', 'autocorrect', 'midline', 'wrap', 'appkeys', 'learn', 'screen')] [string]$Scenario = 'suggest',
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\build\windows\OpenTypist\OpenTypist.exe'),
    [string]$Out = (Join-Path $env:TEMP 'opentypist-typing-test'),
    [int]$KeyDelay = 70,
    # Ctrl+Plus presses before typing (Edge: 110%, 125%, 150%, ...).
    [int]$ZoomSteps = 0
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class TypingTest {
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short VkKeyScanEx(char ch, IntPtr hkl);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    static void Key(ushort vk, bool up, uint extraFlags) {
        var input = new INPUT { type = 1 };
        input.ki.wVk = vk;
        input.ki.dwFlags = (up ? 2u : 0u) | extraFlags;
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void Unicode(char c) {
        foreach (var up in new[] { false, true }) {
            var input = new INPUT { type = 1 };
            input.ki.wScan = c;
            input.ki.dwFlags = 4u | (up ? 2u : 0u);
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    public static IntPtr Window;
    /// Part of the window title while the test file or page is the active tab.
    public static string Title;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);

    static void CheckFocus() {
        var foreground = GetForegroundWindow();
        if (Window != IntPtr.Zero && foreground != Window) {
            var other = new System.Text.StringBuilder(512);
            GetWindowText(foreground, other, other.Capacity);
            var cls = new System.Text.StringBuilder(256);
            GetClassName(foreground, cls, cls.Capacity);
            int pid;
            GetWindowThreadProcessId(foreground, out pid);
            string process = "?";
            try { process = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch (ArgumentException) { }
            throw new InvalidOperationException("Another window took the focus ('" + other + "', " + cls + ", " + process + ") after " + Typed + " characters; stopped typing.");
        }
        if (!string.IsNullOrEmpty(Title)) {
            var text = new System.Text.StringBuilder(512);
            GetWindowText(foreground, text, text.Capacity);
            if (text.ToString().IndexOf(Title, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("The test file or page is no longer the active tab; stopped typing.");
        }
    }

    public static int Typed;

    /// Types like a keyboard: the layout's virtual key, with Shift when needed.
    public static void Type(string text, int delay) {
        int pid;
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out pid));
        Typed = 0;
        foreach (char c in text) {
            CheckFocus();
            Typed++;
            if (c == '\n') { Press(0x0D, false); Thread.Sleep(delay); continue; }
            short scan = VkKeyScanEx(c, layout);
            if (scan == -1 || (scan & 0x600) != 0) { Unicode(c); }
            else {
                bool shift = (scan & 0x100) != 0;
                if (shift) Key(0x10, false, 0);
                Key((ushort)(scan & 0xFF), false, 0);
                Key((ushort)(scan & 0xFF), true, 0);
                if (shift) Key(0x10, true, 0);
            }
            Thread.Sleep(delay);
        }
    }

    public static void Press(ushort vk, bool shift) {
        CheckFocus();
        if (shift) Key(0x10, false, 0);
        Key(vk, false, 0);
        Key(vk, true, 0);
        if (shift) Key(0x10, true, 0);
    }

    /// Human-paced: people let go of Alt tens of milliseconds after the key.
    public static void PressAlt(ushort vk) {
        CheckFocus();
        Key(0x12, false, 0);
        Thread.Sleep(30);
        Key(vk, false, 0);
        Thread.Sleep(30);
        Key(vk, true, 0);
        Thread.Sleep(30);
        Key(0x12, true, 0);
    }

    public static void PressCtrl(ushort vk) {
        CheckFocus();
        Key(0x11, false, 0);
        Key(vk, false, 0);
        Key(vk, true, 0);
        Key(0x11, true, 0);
    }

    public static void PressCtrlShift(ushort vk) {
        CheckFocus();
        Key(0x11, false, 0);
        Key(0x10, false, 0);
        Key(vk, false, 0);
        Key(vk, true, 0);
        Key(0x10, true, 0);
        Key(0x11, true, 0);
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    /// Screen pixels including layered windows (the ghost text overlay).
    public static System.Drawing.Bitmap Capture(int x, int y, int w, int h) {
        var bitmap = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) {
            IntPtr dest = g.GetHdc();
            IntPtr screen = GetDC(IntPtr.Zero);
            BitBlt(dest, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            ReleaseDC(IntPtr.Zero, screen);
            g.ReleaseHdc(dest);
        }
        return bitmap;
    }
}
'@ -ReferencedAssemblies System.Drawing
# Physical pixels, like the app.
[TypingTest]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

$log = Join-Path $env:LOCALAPPDATA 'OpenTypist\log.txt'
if (-not (Get-Process OpenTypist -ErrorAction SilentlyContinue)) {
    $env:OPENTYPIST_LOG = '1'
    Start-Process (Resolve-Path $Exe)
}
$deadline = (Get-Date).AddSeconds(90)
while (-not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'engine: Ready' -Quiet))) {
    if ((Get-Date) -gt $deadline) { throw 'OpenTypist did not report a loaded model within 90 s' }
    Start-Sleep -Milliseconds 500
}

function Get-FieldText {
    $element = [Windows.Automation.AutomationElement]::FocusedElement
    if ($element.Current.IsPassword) { return '<password field>' }
    $pattern = $null
    try {
        if ($element.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
            return ($pattern.DocumentRange.GetText(-1) -replace "`r`n?", "`n")
        }
        if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    } catch { }
    return '<unreadable>'
}

function Save-Screenshot($name) {
    $element = [Windows.Automation.AutomationElement]::FocusedElement
    $r = $element.Current.BoundingRectangle
    $x = [int][Math]::Max(0, $r.X - 10); $y = [int][Math]::Max(0, $r.Y - 10)
    $w = [int][Math]::Min(1400, $r.Width + 20); $h = [int][Math]::Min(500, $r.Height + 20)
    $bitmap = [TypingTest]::Capture($x, $y, $w, $h)
    $file = Join-Path $Out "$Target-$Scenario-$name.png"
    $bitmap.Save($file, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return $file
}

function Show-Log($since) {
    Get-Content $log | Where-Object { $_ -gt $since -and $_ -match 'shown after|no good|caret|field|turned off|password|selected|not enough|administrator|refused|error|Autocorrect|Fixed|Undid|snippet|line\(s\)|card|button|on-screen|model again|Unloaded' } |
        Select-Object -Last 4 | ForEach-Object { "    log: $_" }
}

# Open the target with an empty text field.
if ($Target -eq 'notepad') {
    $file = Join-Path $Out "typing-test-$(Get-Date -Format HHmmss).txt"
    Set-Content -Path $file -Value '' -NoNewline
    $process = Start-Process notepad.exe -ArgumentList "`"$file`"" -PassThru
    $title = [IO.Path]::GetFileNameWithoutExtension($file)
} else {
    $page = Join-Path $Out "$Target.html"
    $field = if ($Target -eq 'edge-password') { '<input type="password" autofocus style="font:16px Segoe UI;width:760px">' }
             elseif ($Scenario -eq 'screen') {
                 '<p><b>From: Sarah Lee</b></p><p>Hi! Could we move our meeting to Thursday at 3pm? The conference room on the fourth floor is booked, so let''s use the small room next to the kitchen.</p>' +
                 '<textarea autofocus style="font:16px Segoe UI;width:760px;height:160px"></textarea>' }
             elseif ($Scenario -eq 'wrap') { '<textarea autofocus style="font:16px Segoe UI;width:420px;height:220px"></textarea>' }
             else { '<textarea autofocus style="font:16px Segoe UI;width:760px;height:220px"></textarea>' }
    # A title of its own, so a test window left from another run is never typed into.
    $title = "OpenTypist typing test $(Get-Date -Format HHmmss)"
    Set-Content -Path $page -Encoding UTF8 -Value "<!doctype html><title>$title</title><body style=`"font:16px Segoe UI;margin:40px`">$field</body>"
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    $process = Start-Process $edge -ArgumentList "--user-data-dir=`"$Out\edge-profile`"", '--no-first-run', '--no-default-browser-check',
        '--hide-crash-restore-bubble', '--disable-session-crashed-bubble', '--new-window', "`"file:///$($page -replace '\\','/')`"" -PassThru
}
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    $window = Get-Process | Where-Object { $_.MainWindowTitle -like "*$title*" } | Select-Object -First 1
} until ($window -or (Get-Date) -gt $deadline)
if (-not $window) { throw "No window titled *$title* appeared" }
# Wait until the test window has kept the focus for a second (browsers show
# popups right after opening).
$deadline = (Get-Date).AddSeconds(15)
$steady = 0
do {
    if ([TypingTest]::GetForegroundWindow() -ne $window.MainWindowHandle) {
        [TypingTest]::SetForegroundWindow($window.MainWindowHandle) | Out-Null
        $steady = 0
    } else { $steady++ }
    Start-Sleep -Milliseconds 250
} until ($steady -ge 4 -or (Get-Date) -gt $deadline)
Start-Sleep -Milliseconds 500
[TypingTest]::Window = [TypingTest]::GetForegroundWindow()
[TypingTest]::Title = $title
for ($i = 0; $i -lt $ZoomSteps; $i++) { [TypingTest]::PressCtrl(0xBB); Start-Sleep -Milliseconds 300 }
$since = (Get-Date).ToString('HH:mm:ss.fff')

$steps = if ($Target -eq 'edge-password') { @(@{ Name = '1-password'; Type = "Thanks for the update! Let me know if you need anything el" }) }
elseif ($Scenario -eq 'autocorrect') { @(
    @{ Name = '1-capitals'; Type = "Teh end. " },
    @{ Name = '2-list-fix'; Type = "`nI think teh " },
    @{ Name = '3-backspace-undo'; Key = 0x08 },
    @{ Name = '4-never-again'; Type = " and teh " },
    @{ Name = '5-model-fix'; Type = "`nI need to go to the stoer "; Wait = 2500 },
    @{ Name = '6-snippet'; Type = "`nmail @@ " }
) }
elseif ($Scenario -eq 'midline') { @(
    @{ Name = '1-typed'; Type = "I am very in this job." },
    @{ Name = '2-caret-back'; Key = 0x25; Repeat = 10 },
    @{ Name = '3-shift-tab'; Key = 0x09; Shift = $true }
) }
elseif ($Scenario -eq 'wrap') { @(
    @{ Name = '1-typed'; Type = "Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so " },
    @{ Name = '2-shift-tab'; Key = 0x09; Shift = $true }
) }
elseif ($Scenario -eq 'learn') {
    $note = "Hi Priya,`nThanks for the notes on Kestrel. I talked to Tomasz and Priya about Kestrel today.`nCheers,`n`n"
    @(1..3 | ForEach-Object { @{ Name = "$_-note"; Type = $note } })
}
elseif ($Scenario -eq 'screen') { @(
    @{ Name = '1-typed'; Type = "Hi Sarah, Thursday at 3pm works for me. I'll meet you in the "; Wait = 2500 },
    # The first read of the window happens in the background; type on.
    @{ Name = '2-typed'; Type = "small room "; Wait = 2500 },
    @{ Name = '3-shift-tab'; Key = 0x09; Shift = $true }
) }
elseif ($Scenario -eq 'appkeys') { @(
    # (Tab itself isn't pressed: with other accept keys it reaches the
    # browser, which moves the focus out of the text area.)
    @{ Name = '1-typed'; Type = "Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so " },
    @{ Name = '2-right'; Key = 0x27 },
    @{ Name = '3-typed'; Type = "`nThanks for the update! Let me know if you need anything el" },
    @{ Name = '4-right'; Key = 0x27 },
    @{ Name = '5-typed'; Type = "`nI am very in" },
    @{ Name = '6-alt-right'; Key = 0x27; Alt = $true }
) }
else { @(
    @{ Name = '1-typed'; Type = "Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so " },
    @{ Name = '2-shift-tab'; Key = 0x09; Shift = $true },
    @{ Name = '3-midword'; Type = "`n`nI am very in" },
    @{ Name = '4-tab'; Key = 0x09 },
    @{ Name = '5-typed'; Type = "`n`nThanks for the update! Let me know if you need anything el" },
    @{ Name = '6-tab'; Key = 0x09 }
) }
foreach ($step in $steps) {
    $before = Get-FieldText
    if ($step.Type) { [TypingTest]::Type($step.Type, $KeyDelay) }
    else {
        $repeat = if ($step.Repeat) { $step.Repeat } else { 1 }
        for ($i = 0; $i -lt $repeat; $i++) {
            if ($step.Alt) { [TypingTest]::PressAlt([uint16]$step.Key) } else { [TypingTest]::Press([uint16]$step.Key, [bool]$step.Shift) }
            Start-Sleep -Milliseconds $KeyDelay
        }
    }
    Start-Sleep -Milliseconds $(if ($step.Wait) { $step.Wait } else { 1200 })
    $after = Get-FieldText
    $shot = Save-Screenshot $step.Name
    $added = if ($after.StartsWith($before)) { $after.Substring($before.Length) } else { "(field now ends with: $($after.Substring([Math]::Max(0, $after.Length - 40))))" }
    "$($step.Name): added $(ConvertTo-Json $added)"
    Show-Log $since
    $since = (Get-Date).ToString('HH:mm:ss.fff')
    "    screenshot: $shot"
}
"Final text: $(ConvertTo-Json (Get-FieldText))"

# Clean up: save and close the Notepad tab, close the test Edge window.
if ($Target -eq 'notepad') {
    [TypingTest]::PressCtrl(0x53)   # Ctrl+S
    Start-Sleep -Milliseconds 500
    [TypingTest]::PressCtrl(0x57)   # Ctrl+W
} else {
    # Ctrl+Shift+W closes the test window; the throwaway profile has only this one.
    [TypingTest]::PressCtrlShift(0x57)
}
