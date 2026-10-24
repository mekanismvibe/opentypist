<#
.SYNOPSIS
Builds OpenTypist for Windows x64 in one go: llama.cpp from vendor/llama.cpp
(same pinned revision as the Mac app) plus the native shim with CMake and
MSVC, then a self-contained `dotnet publish` of the app.

.DESCRIPTION
Output: build\windows\OpenTypist\OpenTypist.exe (run it, or zip the folder).

Needs: Visual Studio 2022 (Build Tools are enough) with "Desktop development
with C++", the .NET 8 SDK, and the Vulkan SDK for the GPU backend. The CPU
backend is always built and is used when the PC has no Vulkan driver.

.PARAMETER NoVulkan
CPU-only build (no Vulkan SDK needed).

.PARAMETER Cuda
Also build the CUDA backend (needs the CUDA toolkit).

.PARAMETER SkipNative
Reuse the native DLLs from the last build.
#>
param(
    [switch]$NoVulkan,
    [switch]$Cuda,
    [switch]$SkipNative,
    [switch]$SkipApp
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path "$PSScriptRoot\..").Path
# Short paths: the Vulkan shader generator nests deep inside the build tree,
# and the MSVC tools stop at 260 characters.
$nativeBuild = Join-Path $root 'build\wn'
$runtime = Join-Path $root 'build\wn\runtime'
$out = Join-Path $root 'build\windows\OpenTypist'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

function Find-Tool($name, [scriptblock]$fallback) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $path = & $fallback
    if ($path -and (Test-Path $path)) { return $path }
    throw "$name not found"
}

function Invoke-Checked($exe) {
    # Windows PowerShell turns a tool's stderr lines into errors; only the
    # exit code decides here.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $exe @args } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

$vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { $null }

if (-not $SkipNative) {
    if (-not (Test-Path (Join-Path $root 'vendor\llama.cpp\CMakeLists.txt'))) {
        & git -C $root submodule update --init --depth 1 vendor/llama.cpp
    }
    if (-not $vsPath) { throw 'Visual Studio 2022 with "Desktop development with C++" is required' }
    # The x64 MSVC environment (cl, link, and Visual Studio's CMake and Ninja).
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
        cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
            if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
        }
    }
    $cmake = Find-Tool 'cmake' { Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }

    $vulkanFlag = if ($NoVulkan) { 'OFF' } else { 'ON' }
    if (-not $NoVulkan -and -not $env:VULKAN_SDK) {
        # A fresh install sets it machine-wide; this shell may predate it.
        $env:VULKAN_SDK = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'Machine')
        if (-not $env:VULKAN_SDK) { throw 'Vulkan SDK not found: winget install KhronosGroup.VulkanSDK, or pass -NoVulkan for a CPU-only build' }
    }
    # (PowerShell names are case-insensitive: $cudaFlag, not $cuda, next to -Cuda.)
    $cudaFlag = if ($Cuda) { 'ON' } else { 'OFF' }

    Write-Host "== llama.cpp $(& git -C (Join-Path $root 'vendor\llama.cpp') rev-parse --short HEAD) (Vulkan $vulkanFlag, CUDA $cudaFlag, CPU always)"
    Invoke-Checked $cmake -S (Join-Path $root 'windows\native') -B $nativeBuild -G Ninja -DCMAKE_BUILD_TYPE=Release `
        "-DOPENTYPIST_VULKAN=$vulkanFlag" "-DOPENTYPIST_CUDA=$cudaFlag"
    Invoke-Checked $cmake --build $nativeBuild --config Release --parallel

    # Collect the DLLs, plus the MSVC runtime so the app runs on PCs without
    # the Visual C++ Redistributable.
    if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
    New-Item -ItemType Directory -Force $runtime | Out-Null
    Copy-Item (Join-Path $nativeBuild 'bin\*.dll') $runtime
    $crt = Get-ChildItem (Join-Path $vsPath 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($crt) {
        foreach ($dll in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
            $file = Join-Path $crt.FullName $dll
            if (Test-Path $file) { Copy-Item $file $runtime }
        }
    }
    Write-Host "Native DLLs: $((Get-ChildItem $runtime -Filter *.dll).Name -join ', ')"
}

if (-not $SkipApp) {
    # The app icon comes from the Mac app's Resources/AppIcon.icns (its PNG
    # images, repacked as a .ico).
    $icon = Join-Path $root 'build\windows-icon\AppIcon.ico'
    & (Join-Path $PSScriptRoot 'tools\icns-to-ico.ps1') -Icns (Join-Path $root 'Resources\AppIcon.icns') -Ico $icon

    $dotnet = Find-Tool 'dotnet' { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    Write-Host '== OpenTypist app'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Invoke-Checked $dotnet publish (Join-Path $PSScriptRoot 'src\OpenTypist\OpenTypist.csproj') -c Release -r win-x64 --self-contained true `
        -o $out "-p:NativeDir=$runtime\" "-p:AppIconPath=$icon" -nologo
    Write-Host "Built $out\OpenTypist.exe"
}
