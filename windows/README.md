# OpenTypist for Windows

The Windows port of OpenTypist, following [docs/windows-port.md](../docs/windows-port.md):
grey ghost-text suggestions after the caret in any app, accepted with Tab
(next word) or Shift+Tab (whole suggestion), generated on your PC by a local
GGUF model through llama.cpp. Nothing you type leaves the computer.

**Status: phase 1** (engine, model download, ghost text, Tab / Shift+Tab, tray
icon, minimal settings). Autocorrect, snippets, per-app keys, the mid-line
card and wrapping are phase 2; personalization, screen context, battery
options, stats, the updater and CI/release are phase 3.

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
.\OpenTypist.exe --download gemma-4-e2b | Out-Host      # download + SHA-256 check from the catalog
```

(`| Out-Host` makes PowerShell wait for the output of this windowed app.)

`$env:OPENTYPIST_LOG = '1'` before starting the app writes the diagnostics
(never typed text) to `%LOCALAPPDATA%\OpenTypist\log.txt`.

`windows\tools\typing-test.ps1 -Target notepad|edge [-ZoomSteps 3]` types into
Notepad or an Edge text area like a person (real key presses), accepts with
Tab and Shift+Tab, and prints what the field contains, the app's diagnostics
and screenshots. Don't touch the keyboard while it runs.

## Measured (phase 1, this PC)

Intel i7-11700F, 32 GB RAM, NVIDIA RTX 3060 12 GB, Windows 11, Vulkan backend.

| | Gemma 4 E2B | Gemma 4 E4B | Gemma 4 E2B on CPU |
| --- | --- | --- | --- |
| Per keystroke, no filter (`--selftest` typing simulation, medium length): median / p90 | 84 / 90 ms | 102 / 145 ms | 550 / 729 ms |
| Per keystroke, Balanced filter: median / p90 | 26 / 43 ms | 43 / 73 ms | 162 / 285 ms |
| Model load (warm file cache) | 2.4 s | 3.2 s | 2.1 s |

In the running app, a suggestion appears a median 32 ms (p90 58 ms) with E2B
and 52 ms (max 96 ms) with E4B after the 60 ms pause that follows each
keystroke; reading the field through UI Automation takes 4 ms in Notepad and
7 ms in Edge.

The first start of a new build on a PC takes 20–35 s longer: the Vulkan
driver compiles its GPU pipelines once (then caches them), which the app does
while loading so the first keystroke doesn't wait.

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
| `OpenTypist/TrayApp.cs` | `AppDelegate.swift` |
| `OpenTypist/SelfTest.cs` | `SelfTest.swift` |
| `native/opentypist_native.c` | (shim for llama.h struct-by-value calls) |
