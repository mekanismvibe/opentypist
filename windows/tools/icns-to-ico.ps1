<#
.SYNOPSIS
Makes the Windows app icon from the Mac app's Resources/AppIcon.icns, so both
apps share one source image: takes the largest PNG inside the .icns and
writes a .ico with the sizes Windows uses (16-256 px, PNG-compressed).
#>
param(
    [Parameter(Mandatory)] [string]$Icns,
    [Parameter(Mandatory)] [string]$Ico
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ((Test-Path $Ico) -and (Get-Item $Ico).LastWriteTime -gt (Get-Item $Icns).LastWriteTime) { return }

$bytes = [IO.File]::ReadAllBytes((Resolve-Path $Icns))
function Read-UInt32BE([byte[]]$b, [int]$i) { ([uint32]$b[$i] -shl 24) -bor ([uint32]$b[$i + 1] -shl 16) -bor ([uint32]$b[$i + 2] -shl 8) -bor [uint32]$b[$i + 3] }

if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'icns') { throw "$Icns is not an .icns file" }
$best = $null
$offset = 8
while ($offset + 8 -le $bytes.Length) {
    $length = [int](Read-UInt32BE $bytes ($offset + 4))
    if ($length -lt 8) { break }
    $dataStart = $offset + 8
    # PNG signature: 89 50 4E 47
    if ($bytes[$dataStart] -eq 0x89 -and $bytes[$dataStart + 1] -eq 0x50 -and $bytes[$dataStart + 2] -eq 0x4E -and $bytes[$dataStart + 3] -eq 0x47) {
        if (-not $best -or $length -gt $best.Length) { $best = @{ Start = $dataStart; Length = $length - 8 } }
    }
    $offset += $length
}
if (-not $best) { throw "No PNG image found in $Icns" }

$source = [Drawing.Image]::FromStream([IO.MemoryStream]::new($bytes, $best.Start, $best.Length))
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path $Ico) | Out-Null
$out = [IO.BinaryWriter]::new([IO.File]::Create($Ico))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$dataOffset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32)
    $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$dataOffset)
    $dataOffset += $images[$i].Length
}
foreach ($image in $images) { $out.Write($image) }
$out.Close()
$source.Dispose()
