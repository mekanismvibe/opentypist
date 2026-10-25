# OpenTypist for Windows

The Windows port of OpenTypist, following [docs/windows-port.md](../docs/windows-port.md):
grey ghost-text suggestions after the caret in any app, accepted with Tab
(next word) or Shift+Tab (whole suggestion), generated on your PC by a local
GGUF model through llama.cpp. Nothing you type leaves the computer.

**Status: phases 1 and 2.** Phase 1: engine, model download, ghost text,
Tab / Shift+Tab, tray icon, settings. Suggestions stream: they appear as soon
as the first word is complete and grow word by word; Tab accepts what's
visible. Phase 2: autocorrect, snippets, accept keys per app, the mid-line
card and wrapping. Phase 3 (personalization, screen context, battery
options, stats, the updater and CI/release) is still to do.

## What it does

- **Suggestions** after the caret in the field's own font and a faded color,
  wrapping like the field's text (up to 4 lines, before a send or emoji
  button on the line). In the middle of a line (text after the caret), a
  short suggestion in a small card below the line that leads into what
  follows ("terested in" before " this job").
- **Accept keys**: Tab (next word), Shift+Tab (whole suggestion), Esc
  dismisses. Both can be changed in Settings, and per app in Settings → Apps
  (for example → and Alt+→ where Tab does something else).
- **Autocorrect**, like the Mac app: Wikipedia's list of common misspellings
  (shared with the Mac app in `Resources/autocorrect`) fixes words as they
  end; the model settles the cases the list can't (several possible fixes, a
  "misspelling" that is also a real word, words the Windows spell checker
  doesn't know). Backspace right after a fix undoes it, and that word is then
  never corrected. Import more lists (`misspelling->fix` or AutoHotkey
  `::typo::fix` scripts) in Settings → Autocorrect.
- **Snippets**: a shortcut followed by a space or punctuation becomes its
  text (`@@` → your email address), in every app; Backspace undoes it.
- Personal data (snippets, the never-correct list) is encrypted with DPAPI
  for your Windows account, in `%APPDATA%\OpenTypist\Personal`.

How text gets typed matters on Windows, where apps read injected keys late
when they are busy: characters go out as the keyboard layout's own keys
(Unicode events only for characters the layout can't type, spaced out),
words one at a time 30 ms apart (Notepad's spell check drops text that
arrives right after a finished word), modifiers the user holds are released
first, and swallowing the key of an Alt+→ accept presses an unassigned key
so Windows doesn't open the app's menu. Keys typed while a correction is
going out are held back and replayed after it.

## Build

Requirements (once):

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
winget install Microsoft.DotNet.SDK.8
winget install KhronosGroup.VulkanSDK
```

Then, from the repository root (with submodules: `git clone --recursive`):

```powershell
.\windows\build.ps1            # Vulkan + CPU backends
.\windows\build.ps1 -NoVulkan  # CPU only, no Vulkan SDK needed
.\windows\build.ps1 -Cuda      # also the CUDA backend (needs the CUDA toolkit)
.\windows\build.ps1 -SkipNative  # rebuild only the app
.\windows\build.ps1 -Out build\windows-dev\OpenTypist  # somewhere else (e.g. while the app is running)
```

The result is a self-contained x64 folder, `build\windows\OpenTypist\`
(no .NET install needed to run it). llama.cpp is built from the pinned
`vendor/llama.cpp` submodule, the same revision as the Mac app.

## Run

Start `build\windows\OpenTypist\OpenTypist.exe`. It lives in the notification
area (tray); on the first start it opens Settings → Model so you can pick a
model (nothing downloads until you do). The recommended model depends on the
PC's memory and graphics card. Models are stored in
`%LOCALAPPDATA%\OpenTypist\Models`, settings in `%APPDATA%\OpenTypist\settings.json`.

The tray menu has the same items as the Mac menu bar icon: status, "Last
check" (why the last keystroke did or didn't give a suggestion), enable,
disable in the current app, suggestion length, add space after accepting,
Settings and Quit. Opening the app again shows Settings.

Unsigned builds show "Windows protected your PC" (SmartScreen) until there is
a code-signing certificate: click "More info", then "Run anyway".

## Self-test and tools

```powershell
.\OpenTypist.exe --selftest | Out-Host                  # load the model, sample suggestions, per-keystroke latency, logic checks
.\OpenTypist.exe --selftest --model C:\path\model.gguf --cpu | Out-Host
.\OpenTypist.exe --selftest-offline | Out-Host          # logic checks only (no model, for CI)
.\OpenTypist.exe --calibrate [--model x.gguf] | Out-Host  # quality per confidence threshold and per-keystroke latency
.\OpenTypist.exe --download gemma-4-e2b | Out-Host      # download + SHA-256 check from the catalog
```

(`| Out-Host` makes PowerShell wait for the output of this windowed app.)

`$env:OPENTYPIST_LOG = '1'` before starting the app writes the diagnostics
(never typed text) to `%LOCALAPPDATA%\OpenTypist\log.txt`.

`windows\tools\typing-test.ps1 -Target notepad|edge|edge-password [-Scenario suggest|autocorrect|midline|wrap|appkeys] [-ZoomSteps 3]`
types into Notepad or an Edge text area like a person (real key presses),
accepts suggestions, and prints what the field contains, the app's
diagnostics and screenshots (`edge-password` checks that a password field gets
no suggestion). Don't touch the keyboard while it runs; it stops if the test
window or tab loses the focus.

`$env:OPENTYPIST_TEST_STORE = 'C:\some\folder'` before starting the app keeps
settings and personal data in that folder instead of `%APPDATA%\OpenTypist`,
so tests never touch your own.

## Measured (phase 1, this PC)

Intel i7-11700F, 32 GB RAM, NVIDIA RTX 3060 12 GB, Windows 11, Vulkan backend.

`--selftest` typing simulation (one keystroke at a time, medium length), median / p90:

| | E2B Q4_0, GPU | E2B Q4_K_M, GPU | E4B, GPU | E2B Q4_0, CPU | E2B Q4_K_M, CPU |
| --- | --- | --- | --- | --- | --- |
| Whole suggestion, no filter | 77 / 143 ms | 80 / 88 ms | 102 / 145 ms | 502 / 897 ms | 537 / 701 ms |
| Whole suggestion, Balanced | 17 / 40 ms | 26 / 43 ms | 43 / 73 ms | 107 / 255 ms | 153 / 276 ms |
| First word on screen (streaming), Balanced | 17 / 25 ms | 19 / 27 ms | 32 / 45 ms | 148 / 193 ms | 157 / 209 ms |
| Model load (warm file cache) | 2.0 s | 2.0 s | 3.3 s | 1.6 s | 2.2 s |

In the running app (E4B, streaming), the first word appears 42–48 ms after
the 60 ms pause that follows each keystroke (73 ms for the first suggestion in
a field) and the whole suggestion at 53–99 ms; reading the field through UI
Automation takes 4 ms in Notepad and 6–11 ms in Edge.

### Gemma 4 E2B: Q4_0 or Q4_K_M

The brief asks to check the Mac's switch to Q4_0 on Windows GPUs. `--calibrate`
(283 positions in 7 messages), two runs per format on the RTX 3060 (Vulkan):

| Balanced | Q4_K_M | Q4_0 |
| --- | --- | --- |
| Suggestion shown / first word right / suggested words right | 73.9% / 78.9% / 50.1% | 72.4% / 80.0% / 49.3% |
| Half-typed words finished (0.10): shown / right | 99.6% / 87.3% | 100% / 86.5% |
| Per keystroke: median / p90 | 31.2–31.4 / 76.5–78.1 ms | 24.6–28.0 / 67.3–72.7 ms |
| First word: median / p90 | 18.2 / 24.1–24.2 ms | 17.3–17.8 / 22.9–23.8 ms |

Same quality (Q4_K_M reproduces the Mac's 74% / 50% at Balanced). Q4_0 is as
fast or slightly faster on Vulkan, about 30% faster on the CPU at Balanced, and
65 MB smaller, so Windows downloads Q4_0 like the Mac. An existing Q4_K_M
download keeps working.

The first start of a new build on a PC takes 20–35 s longer: the Vulkan
driver compiles its GPU pipelines once (then caches them), which the app does
while loading so the first keystroke doesn't wait.

### Phase 2

Checked with `tools/typing-test.ps1` in Notepad and an Edge text area:

| Scenario | Result |
| --- | --- |
| Autocorrect: `Teh end. ` / `I think teh ` | `The end. ` / `I think the ` |
| Backspace right after the fix | back to `teh`; typing `teh ` again leaves it |
| Model fix: `I need to go to the stoer ` | `store` (a moment later) |
| Snippet: `mail @@ ` | `mail me@example.com ` |
| Mid-line: `I am very in` before ` this job.`, Shift+Tab | card below the line, then `I am very interested in this job.` |
| Wrapping (Notepad, 34-character suggestion near the edge) | two lines, the second from the line start; Shift+Tab inserts all of it |
| Per-app keys in Edge (→, Alt+→) | → takes the next word, Alt+→ the whole suggestion, no menu opens |
| Password field | no suggestion (54 of 54 keystrokes skipped) |

The Windows spell checker takes 0.44 ms per word (it runs in the keyboard
hook at the end of each word); scoring candidates with the model 8–36 ms.

## Code map

| Windows (`windows/src`) | Mac reference (`Sources/OpenTypist`) |
| --- | --- |
| `OpenTypist.Core/LlamaEngine.cs`, `Native.cs` | `LlamaEngine.swift` |
| `OpenTypist.Core/EngineService.cs` | `EngineService.swift` |
| `OpenTypist.Core/PromptBuilder.cs`, `Suggestion.cs` | `Suggestion.swift` |
| `OpenTypist.Core/ModelCatalog.cs`, `ModelManager.cs` | `ModelCatalog.swift`, `ModelManager.swift` |
| `OpenTypist.Core/CompletionOptions.cs` | `CompletionLength` and `ConfidenceLevel` in `Settings.swift` |
| `OpenTypist/CompletionController.cs` | `CompletionController.swift` |
| `OpenTypist/FocusReader.cs` (UI Automation) | `FocusReader.swift` (Accessibility) |
| `OpenTypist/KeyboardHook.cs`, `TextInserter.cs` | `KeyboardMonitor.swift` |
| `OpenTypist/OverlayWindow.cs` | `OverlayWindow.swift` |
| `OpenTypist/AcceptKeys.cs` | `AcceptKeys.swift` |
| `OpenTypist.Core/AutocorrectRules.cs`, `Typos.cs` | `AutocorrectRules.swift`, `SpellCheck` in `Autocorrector.swift` |
| `OpenTypist/Autocorrector.cs`, `SpellCheck.cs` (Windows spell checker) | `Autocorrector.swift` |
| `OpenTypist/TextReplacements.cs` | `TextReplacements.swift` |
| `OpenTypist/SecureStore.cs` (DPAPI) | `SecureStore.swift` (Keychain) |
| `OpenTypist/SettingsWindow*.cs` | `SettingsView.swift`, `ReplacementsSettingsView.swift` |
| `OpenTypist/TrayApp.cs` | `AppDelegate.swift` |
| `OpenTypist/SelfTest.cs` | `SelfTest.swift` |
| `native/opentypist_native.c` | (shim for llama.h struct-by-value calls) |
