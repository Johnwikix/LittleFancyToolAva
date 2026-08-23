param(
    [string]$SourceIcon = (Join-Path $PSScriptRoot "..\FancyTool\Assets\icon.png"),
    [string]$OutputDir  = (Join-Path $PSScriptRoot "Images")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $SourceIcon)) { throw "Source icon not found: $SourceIcon" }
if (-not (Test-Path $OutputDir))  { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

$sizes = [ordered]@{
    "StoreLogo.png"                                       = New-Object System.Drawing.Size 50, 50
    "Square44x44Logo.png"                                 = New-Object System.Drawing.Size 44, 44
    "Square44x44Logo.targetsize-24_altform-unplated.png"  = New-Object System.Drawing.Size 24, 24
    "Square150x150Logo.png"                               = New-Object System.Drawing.Size 150, 150
    "Wide310x150Logo.png"                                 = New-Object System.Drawing.Size 310, 150
    "SplashScreen.png"                                    = New-Object System.Drawing.Size 620, 300
    "LockScreenLogo.png"                                  = New-Object System.Drawing.Size 24, 24
}

$bmp = [System.Drawing.Bitmap]::FromFile($SourceIcon)
Write-Host ("Source: {0}x{1} -> 7 target sizes" -f $bmp.Width, $bmp.Height)

foreach ($kv in $sizes.GetEnumerator()) {
    $out = Join-Path $OutputDir $kv.Key
    $canvas = New-Object System.Drawing.Bitmap $kv.Value.Width, $kv.Value.Height
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($bmp, 0, 0, $kv.Value.Width, $kv.Value.Height)
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $canvas.Dispose()
    Write-Host ("  + {0} ({1}x{2})" -f $kv.Key, $kv.Value.Width, $kv.Value.Height)
}
$bmp.Dispose()
Write-Host "Done"