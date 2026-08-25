#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads Real-ESRGAN ONNX models used by FancyTool's Super Resolution feature.

.DESCRIPTION
    Fetches the three Super Resolution models (all FLOAT16 precision) into the
    FancyTool/Assets/Models directory. Models are not committed to the repository;
    this script is the supported way to obtain them. Files already present are
    verified against their pinned SHA256 and re-downloaded on mismatch.

.NOTES
    The official xinntao/Real-ESRGAN releases ship only PyTorch (.pth) weights,
    not ONNX. This script uses pre-converted fp16 ONNX exports hosted at
    huggingface.co/universonic/RealESRGAN (BSD-3-Clause), which preserve the
    original architectures and the expected preprocessing contract:
        Input:  NCHW FLOAT16 RGB, range [0, 1], dynamic H/W
        Output: NCHW FLOAT16 RGB, range [0, 1], 4x the spatial dimensions
#>

[CmdletBinding()]
param(
    [string]$ModelsDir = (Join-Path $PSScriptRoot "..\FancyTool\Assets\Models")
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Download-Model {
    param(
        [string]$Url,
        [string]$Destination,
        [string]$Sha256
    )

    if (Test-Path -LiteralPath $Destination) {
        $existingHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if (-not [string]::IsNullOrEmpty($Sha256) -and $existingHash -eq $Sha256) {
            Write-Host "    Verified (already present): $Destination" -ForegroundColor DarkGray
            return
        }
        Write-Host "    Present but hash mismatch; re-downloading: $Destination" -ForegroundColor Yellow
    }

    Write-Host "    Downloading: $Url"
    $tmp = "$Destination.download"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 900
    }
    catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw
    }

    $hash = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash
    if (-not [string]::IsNullOrEmpty($Sha256) -and $hash -ne $Sha256) {
        Remove-Item -LiteralPath $tmp -Force
        throw "SHA256 mismatch for $Destination. Expected $Sha256, got $hash."
    }

    Move-Item -LiteralPath $tmp -Destination $Destination -Force
    Write-Host "    Verified (SHA256 OK): $Destination" -ForegroundColor Green
    if ([string]::IsNullOrEmpty($Sha256)) {
        Write-Host "    SHA256: $hash" -ForegroundColor Yellow
    }
}

if (-not (Test-Path -LiteralPath $ModelsDir)) {
    New-Item -ItemType Directory -Path $ModelsDir -Force | Out-Null
}

Write-Step "Resolving target directory: $ModelsDir"

# RealESRGAN_x4plus (general, fp16, ~33 MB)
$x4plus = @{
    Url         = "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_fp16.onnx"
    Destination = Join-Path $ModelsDir "RealESRGAN_x4plus.onnx"
    Sha256      = "30F8DCE72DD67F2F5C492CDEC6FFE1E684833D9F82E3CB1284184710831CD960"
}

# RealESRGAN_x4plus_anime_6B (anime, fp16, ~9 MB)
$x4plusAnime = @{
    Url         = "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_anime_6B_fp16.onnx"
    Destination = Join-Path $ModelsDir "RealESRGAN_x4plus_anime.onnx"
    Sha256      = "38AB81F8F9B5C8B9E03EEAB8BE2F690FE2EE448AC5603174B6DD9B49B6205A24"
}

# realesr-general-x4v3 (lightweight general, fp16, ~5 MB)
$general = @{
    Url         = "https://huggingface.co/universonic/RealESRGAN/resolve/main/realesr-general-x4v3_fp16.onnx"
    Destination = Join-Path $ModelsDir "realesr-general-x4v3_fp16.onnx"
    Sha256      = "CE89B494B6ADAD237792C31D1012D28604BB22D6CD06B8B5903713D4ED636117"
}

Download-Model @x4plus
Download-Model @x4plusAnime
Download-Model @general

Write-Step "All super-resolution models are in place." -ForegroundColor Green