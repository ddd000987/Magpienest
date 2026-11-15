<#
.SYNOPSIS
    Generates the Magpienest test data tree (illustrations + comics).

.DESCRIPTION
    Every generated image shows its own file name in large text on a coloured
    background, so ordering and identity are visible at a glance.
    Runs under Windows PowerShell 5.1 (System.Drawing). Idempotent: files and
    zips that already exist are skipped. Nothing outside -OutDir is touched.

    This file is intentionally pure ASCII (PowerShell 5.1 reads BOM-less files
    as ANSI); non-ASCII names are built from character codes.

.PARAMETER OutDir
    Output root. Receives <OutDir>\illustrations and <OutDir>\comics.

.PARAMETER Count
    Total number of files to produce under illustrations (default 300).
    set-b is used to top up to this number.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-test-data.ps1 -OutDir artifacts\testdata
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir,

    [int]$Count = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- paths
if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path (Get-Location).ProviderPath $OutDir
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$illDir = Join-Path $OutDir 'illustrations'
$comicDir = Join-Path $OutDir 'comics'
[void][System.IO.Directory]::CreateDirectory($illDir)
[void][System.IO.Directory]::CreateDirectory($comicDir)

# ---------------------------------------------------------------- drawing helpers
$script:fontFamily = 'Arial'
foreach ($candidate in @('Microsoft JhengHei', 'Yu Gothic', 'Arial')) {
    if ([System.Drawing.FontFamily]::Families | Where-Object { $_.Name -eq $candidate }) {
        $script:fontFamily = $candidate
        break
    }
}

$script:jpegCodec = @([System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/jpeg' })[0]
$script:jpegParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$script:jpegParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [long]85)

function Get-IndexColor {
    param([int]$Index)
    # HSV -> RGB with fixed saturation/value so white text stays readable.
    $hue = ($Index * 47) % 360
    $s = 0.6
    $v = 0.55
    $c = $v * $s
    $x = $c * (1 - [Math]::Abs((($hue / 60.0) % 2) - 1))
    $m = $v - $c
    switch ([int][Math]::Floor($hue / 60)) {
        0 { $r = $c; $g = $x; $b = 0 }
        1 { $r = $x; $g = $c; $b = 0 }
        2 { $r = 0; $g = $c; $b = $x }
        3 { $r = 0; $g = $x; $b = $c }
        4 { $r = $x; $g = 0; $b = $c }
        default { $r = $c; $g = 0; $b = $x }
    }
    return [System.Drawing.Color]::FromArgb(
        [int](($r + $m) * 255), [int](($g + $m) * 255), [int](($b + $m) * 255))
}

function New-LabelBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [string]$Label,
        [int]$Index,
        [string]$TopLabel = ''
    )
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $bg = Get-IndexColor $Index
        $g.Clear($bg)

        # Border, so crops and letterboxing are easy to spot.
        $borderW = [Math]::Max(1, [int]([Math]::Min($Width, $Height) * 0.01))
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$borderW)
        try {
            $g.DrawRectangle($pen, [int]($borderW / 2), [int]($borderW / 2), $Width - $borderW, $Height - $borderW)
        }
        finally { $pen.Dispose() }

        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $black = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160, 0, 0, 0))
        try {
            $items = @(, @($Label, 0.25, [System.Drawing.RectangleF]::new(0, 0, $Width, $Height)))
            if ($TopLabel -ne '') {
                $items += , @($TopLabel, 0.08, [System.Drawing.RectangleF]::new(0, [single]($Height * 0.02), $Width, [single]($Height * 0.12)))
            }
            foreach ($item in $items) {
                $text = [string]$item[0]
                $rect = [System.Drawing.RectangleF]$item[2]
                $size = [Math]::Max(1.0, $Height * [double]$item[1])
                $font = New-Object System.Drawing.Font($script:fontFamily, [single]$size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
                $measured = $g.MeasureString($text, $font)
                $maxW = $Width * 0.9
                if ($measured.Width -gt $maxW) {
                    $size = [Math]::Max(1.0, $size * $maxW / $measured.Width)
                    $font.Dispose()
                    $font = New-Object System.Drawing.Font($script:fontFamily, [single]$size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
                }
                try {
                    $shadow = [System.Drawing.RectangleF]::new($rect.X + [single]($size * 0.04), $rect.Y + [single]($size * 0.04), $rect.Width, $rect.Height)
                    $g.DrawString($text, $font, $black, $shadow, $fmt)
                    $g.DrawString($text, $font, $white, $rect, $fmt)
                }
                finally { $font.Dispose() }
            }
        }
        finally {
            $white.Dispose()
            $black.Dispose()
            $fmt.Dispose()
        }
    }
    finally { $g.Dispose() }
    return $bmp
}

function Save-Bitmap {
    # $Target is a file path (string) or a Stream; the format follows $Name's extension.
    param($Bitmap, $Target, [string]$Name)
    $ext = [System.IO.Path]::GetExtension($Name).ToLowerInvariant()
    if ($ext -eq '.jpg' -or $ext -eq '.jpeg') {
        $Bitmap.Save($Target, $script:jpegCodec, $script:jpegParams)
    }
    else {
        $Bitmap.Save($Target, [System.Drawing.Imaging.ImageFormat]::Png)
    }
}

function New-TestImage {
    # Creates one image file unless it already exists.
    param(
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [int]$Index,
        [string]$TopLabel = '',
        [int]$Orientation = 0
    )
    if ([System.IO.File]::Exists($Path)) { return }
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path))
    $name = [System.IO.Path]::GetFileName($Path)
    $bmp = New-LabelBitmap -Width $Width -Height $Height -Label $name -Index $Index -TopLabel $TopLabel
    try {
        if ($Orientation -gt 0) {
            # PropertyItem has no public constructor.
            $p = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject([System.Drawing.Imaging.PropertyItem])
            $p.Id = 0x0112
            $p.Type = 3
            $p.Len = 2
            $p.Value = [byte[]](($Orientation -band 0xFF), 0)
            $bmp.SetPropertyItem($p)
        }
        Save-Bitmap $bmp $Path $name
    }
    finally { $bmp.Dispose() }
}

function New-TestZip {
    # $Pages: array of @{ Name = 'ch1/1.jpg'; W = 600; H = 900 }. Skipped if the zip exists.
    param([string]$ZipPath, $Pages)
    if ([System.IO.File]::Exists($ZipPath)) { return }
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($ZipPath))
    $tmp = $ZipPath + '.tmp'
    if ([System.IO.File]::Exists($tmp)) { [System.IO.File]::Delete($tmp) }
    $fs = [System.IO.File]::Open($tmp, [System.IO.FileMode]::Create)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $i = 0
            foreach ($page in $Pages) {
                $i++
                $bmp = New-LabelBitmap -Width $page.W -Height $page.H -Label $page.Name -Index $i
                $ms = New-Object System.IO.MemoryStream
                try {
                    Save-Bitmap $bmp $ms $page.Name
                    $entry = $zip.CreateEntry($page.Name, [System.IO.Compression.CompressionLevel]::Fastest)
                    $es = $entry.Open()
                    try { $ms.WriteTo($es) }
                    finally { $es.Dispose() }
                }
                finally {
                    $ms.Dispose()
                    $bmp.Dispose()
                }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $fs.Dispose() }
    [System.IO.File]::Move($tmp, $ZipPath)
}

# ---------------------------------------------------------------- illustrations
$setA = 120
$deepDirs = @('level1', 'level1\level2', 'level1\level2\level3')
$deepPer = 3
$uniFolder = -join ([char[]](0x540D, 0x524D, 0x30C6, 0x30B9, 0x30C8))   # name test (katakana/kanji)
$uniPrefix = -join ([char[]](0x63D2, 0x756B))                           # illustration (traditional)
$uniCount = 5
$oddNames = @('100%_done [v2] #1.png', '100%_done [v2] #2.jpg', 'a%25b & c.png', "it's (1) {x} @y.png")
$fixed = $setA + ($deepDirs.Count * $deepPer) + $uniCount + $oddNames.Count + 2 + 1 + 1
$setB = [Math]::Max(6, $Count - $fixed)
$portraitCount = [int][Math]::Ceiling($setB / 2.0)
$landscapeCount = $setB - $portraitCount

Write-Host "Illustrations: set-a ($setA)"
$setADir = Join-Path $illDir 'set-a'
for ($i = 1; $i -le $setA; $i++) {
    New-TestImage -Path (Join-Path $setADir "$i.png") -Width 800 -Height 600 -Index $i
}

Write-Host "Illustrations: set-b ($portraitCount portrait, $landscapeCount landscape)"
$setBDir = Join-Path $illDir 'set-b'
for ($i = 1; $i -le $portraitCount; $i++) {
    New-TestImage -Path (Join-Path $setBDir ('portrait-{0:D3}.jpg' -f $i)) -Width 1200 -Height 1800 -Index $i
}
for ($i = 1; $i -le $landscapeCount; $i++) {
    New-TestImage -Path (Join-Path $setBDir ('landscape-{0:D3}.jpg' -f $i)) -Width 1800 -Height 1200 -Index ($i + 5)
}

Write-Host 'Illustrations: deep, non-ASCII, odd names'
$n = 0
foreach ($d in $deepDirs) {
    $levelName = Split-Path $d -Leaf
    for ($i = 1; $i -le $deepPer; $i++) {
        $n++
        $ext = if ($i % 2 -eq 1) { 'png' } else { 'jpg' }
        New-TestImage -Path (Join-Path (Join-Path $illDir ('deep\' + $d)) "$levelName-$i.$ext") -Width 800 -Height 600 -Index $n
    }
}
for ($i = 1; $i -le $uniCount; $i++) {
    New-TestImage -Path (Join-Path (Join-Path $illDir $uniFolder) ('{0}_{1:D3}.png' -f $uniPrefix, $i)) -Width 800 -Height 600 -Index $i
}
$n = 0
foreach ($odd in $oddNames) {
    $n++
    New-TestImage -Path (Join-Path (Join-Path $illDir 'odd names') $odd) -Width 800 -Height 600 -Index $n
}

Write-Host 'Illustrations: big, broken, orientation'
New-TestImage -Path (Join-Path $illDir 'big\huge-8000x6000.jpg') -Width 8000 -Height 6000 -Index 3
[System.GC]::Collect()
New-TestImage -Path (Join-Path $illDir 'big\tiny-16x16.png') -Width 16 -Height 16 -Index 5

$brokenPath = Join-Path $illDir 'broken\not-an-image.jpg'
if (-not [System.IO.File]::Exists($brokenPath)) {
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($brokenPath))
    [System.IO.File]::WriteAllText($brokenPath, 'This is a text file with a .jpg extension. It is not an image.')
}

New-TestImage -Path (Join-Path $illDir 'orientation\exif-rotate-6.jpg') -Width 1600 -Height 1000 -Index 8 -TopLabel 'rotate-6 TOP' -Orientation 6

# ---------------------------------------------------------------- comics
Write-Host 'Comics: Series A'
$vol1 = Join-Path $comicDir 'Series A\Vol 1'
for ($i = 1; $i -le 24; $i++) {
    if ($i -eq 7 -or $i -eq 15) { $w = 2000; $h = 1500 } else { $w = 1000; $h = 1500 }
    New-TestImage -Path (Join-Path $vol1 "$i.jpg") -Width $w -Height $h -Index $i
}
$vol2 = Join-Path $comicDir 'Series A\Vol 2'
for ($i = 1; $i -le 20; $i++) {
    New-TestImage -Path (Join-Path $vol2 "$i.jpg") -Width 1000 -Height 1500 -Index ($i + 10)
}

Write-Host 'Comics: Single Book'
$single = Join-Path $comicDir 'Single Book'
for ($i = 1; $i -le 10; $i++) {
    New-TestImage -Path (Join-Path $single ('{0:D3}.png' -f $i)) -Width 800 -Height 1200 -Index ($i + 20)
}

Write-Host 'Comics: zipped'
$zipDir = Join-Path $comicDir 'zipped'
$cbzPages = @()
for ($i = 1; $i -le 12; $i++) {
    $cbzPages += , @{ Name = "$i.jpg"; W = 600; H = 900 }
}
New-TestZip -ZipPath (Join-Path $zipDir 'Book Z.cbz') -Pages $cbzPages

$nestedPages = @()
foreach ($ch in @('ch1', 'ch2')) {
    for ($i = 1; $i -le 5; $i++) {
        $nestedPages += , @{ Name = ('{0}/{1:D3}.jpg' -f $ch, $i); W = 600; H = 900 }
    }
}
New-TestZip -ZipPath (Join-Path $zipDir 'Nested.zip') -Pages $nestedPages

# ---------------------------------------------------------------- summary (counted from disk)
$imageExt = @('.jpg', '.jpeg', '.png')
$illFiles = @(Get-ChildItem -LiteralPath $illDir -Recurse -File)
$comicBooks = @(Get-ChildItem -LiteralPath $comicDir -Recurse -Directory | Where-Object {
        @(Get-ChildItem -LiteralPath $_.FullName -File | Where-Object { $imageExt -contains $_.Extension.ToLowerInvariant() }).Count -gt 0
    })
$zipFiles = @(Get-ChildItem -LiteralPath $comicDir -Recurse -File | Where-Object { @('.zip', '.cbz') -contains $_.Extension.ToLowerInvariant() })
$allFiles = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)

Write-Host ''
Write-Host "Output:                  $OutDir"
Write-Host "Illustration files:      $($illFiles.Count)"
Write-Host "Comic books (folders):   $($comicBooks.Count)"
Write-Host "Zip files (zip/cbz):     $($zipFiles.Count)"
Write-Host "Total files:             $($allFiles.Count)"
