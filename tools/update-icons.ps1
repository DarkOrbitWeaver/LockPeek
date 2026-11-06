#requires -Version 5.1
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$brainDir = "$env:USERPROFILE\.gemini\antigravity\brain\0b75c0c3-505c-45bf-a8c5-aff6cf96a557"
$concept1 = Join-Path $brainDir "concept-1.png"
$concept4 = Join-Path $brainDir "concept-4.png"
$concept5 = Join-Path $brainDir "concept-5.png"

if (!(Test-Path $concept1) -or !(Test-Path $concept4) -or !(Test-Path $concept5)) {
    throw "Source concept images not found in artifact directory."
}

function Resize-Image([System.Drawing.Image]$source, [int]$width, [int]$height) {
    $dest = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $dest.SetResolution($source.HorizontalResolution, $source.VerticalResolution)
    $g = [System.Drawing.Graphics]::FromImage($dest)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $width, $height)
    $g.Dispose()
    return $dest
}

function Save-Png([System.Drawing.Image]$img, [string]$path) {
    $dir = Split-Path $path -Parent
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function New-IcoFile([string[]]$pngPaths, [string]$icoPath) {
    $frames = @()
    foreach ($p in $pngPaths) {
        $bytes = [System.IO.File]::ReadAllBytes($p)
        $bmp = [System.Drawing.Image]::FromFile($p)
        $w = $bmp.Width
        $h = $bmp.Height
        $bmp.Dispose()
        $frames += [PSCustomObject]@{
            Width = $w
            Height = $h
            Bytes = $bytes
        }
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # ICONDIR header
    $bw.Write([uint16]0) # Reserved
    $bw.Write([uint16]1) # Type (1 = ICO)
    $bw.Write([uint16]$frames.Count) # Number of images

    $offset = 6 + ($frames.Count * 16)

    # ICONDIRENTRY headers
    foreach ($f in $frames) {
        $wByte = if ($f.Width -eq 256) { [byte]0 } else { [byte]$f.Width }
        $hByte = if ($f.Height -eq 256) { [byte]0 } else { [byte]$f.Height }
        $bw.Write($wByte)
        $bw.Write($hByte)
        $bw.Write([byte]0) # Color count
        $bw.Write([byte]0) # Reserved
        $bw.Write([uint16]1) # Color planes
        $bw.Write([uint16]32) # Bits per pixel
        $bw.Write([uint32]$f.Bytes.Length) # Image size in bytes
        $bw.Write([uint32]$offset) # Image offset
        $offset += $f.Bytes.Length
    }

    # Image data
    foreach ($f in $frames) {
        $bw.Write($f.Bytes)
    }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$img1 = [System.Drawing.Image]::FromFile($concept1)
$img4 = [System.Drawing.Image]::FromFile($concept4)
$img5 = [System.Drawing.Image]::FromFile($concept5)

Write-Host "Updating Store promotional banner (1920x1080)..."
$hero = Resize-Image $img5 1920 1080
Save-Png $hero (Join-Path $root "assets/store/lockpeek-store-hero-1920x1080.png")
$hero.Dispose()

Write-Host "Updating Store master square tile (1024x1024)..."
$store1024 = Resize-Image $img4 1024 1024
Save-Png $store1024 (Join-Path $root "assets/store/lockpeek-store-1024.png")
$store1024.Dispose()

Write-Host "Updating Brand logo master..."
Save-Png $img1 (Join-Path $root "assets/brand/lockpeek-logo-master.png")

Write-Host "Rendering taskbar multi-size PNGs..."
$taskbarPngs = @()
$icoSizes = @(16, 20, 24, 32, 40, 48, 64, 256)
foreach ($sz in $icoSizes) {
    $resized = Resize-Image $img1 $sz $sz
    $darkPath = Join-Path $root "assets/taskbar/lockpeek-dark-$sz.png"
    $lightPath = Join-Path $root "assets/taskbar/lockpeek-light-$sz.png"
    Save-Png $resized $darkPath
    Save-Png $resized $lightPath
    $taskbarPngs += $darkPath
    $resized.Dispose()
}

Write-Host "Generating taskbar and brand ICO files..."
$darkIco = Join-Path $root "assets/taskbar/lockpeek-dark.ico"
$lightIco = Join-Path $root "assets/taskbar/lockpeek-light.ico"
$brandIco = Join-Path $root "assets/brand/lockpeek.ico"
New-IcoFile $taskbarPngs $darkIco
New-IcoFile $taskbarPngs $lightIco
New-IcoFile $taskbarPngs $brandIco

Write-Host "Rendering MSIX package assets..."
# StoreLogo: 50x50, 100x100, 200x200
foreach ($scale in @(@{Name="StoreLogo.png"; Size=50}, @{Name="StoreLogo.scale-200.png"; Size=100}, @{Name="StoreLogo.scale-400.png"; Size=200})) {
    $r = Resize-Image $img4 $scale.Size $scale.Size
    Save-Png $r (Join-Path $root "assets/msix/$($scale.Name)")
    $r.Dispose()
}

# Square150x150Logo: 150x150, 300x300, 600x600
foreach ($scale in @(@{Name="Square150x150Logo.png"; Size=150}, @{Name="Square150x150Logo.scale-200.png"; Size=300}, @{Name="Square150x150Logo.scale-400.png"; Size=600})) {
    $r = Resize-Image $img4 $scale.Size $scale.Size
    Save-Png $r (Join-Path $root "assets/msix/$($scale.Name)")
    $r.Dispose()
}

# Square44x44Logo base & scales (Transparent unplated lock)
foreach ($scale in @(@{Name="Square44x44Logo.png"; Size=44}, @{Name="Square44x44Logo.scale-200.png"; Size=88}, @{Name="Square44x44Logo.scale-400.png"; Size=176})) {
    $r = Resize-Image $img1 $scale.Size $scale.Size
    Save-Png $r (Join-Path $root "assets/msix/$($scale.Name)")
    $r.Dispose()
}

# Square44x44Logo & AppList target sizes
$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)
foreach ($sz in $targetSizes) {
    $r = Resize-Image $img1 $sz $sz
    foreach ($prefix in @("Square44x44Logo", "AppList")) {
        Save-Png $r (Join-Path $root "assets/msix/$prefix.targetsize-$sz.png")
        Save-Png $r (Join-Path $root "assets/msix/$prefix.targetsize-${sz}_altform-unplated.png")
        Save-Png $r (Join-Path $root "assets/msix/$prefix.targetsize-${sz}_altform-lightunplated.png")
    }
    $r.Dispose()
}

$img1.Dispose()
$img4.Dispose()
$img5.Dispose()

Write-Host "All assets successfully updated!"
