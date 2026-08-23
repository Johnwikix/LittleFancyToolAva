param(
    [string]$Configuration = "Release",
    [ValidateSet("x64")] [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$psExe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
if (-not $psExe) { $psExe = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source }
if (-not $psExe) { throw "Neither pwsh nor powershell found in PATH." }

& $psExe -NoProfile -File "$root\FancyToolAva.Msix\generate-msix-icons.ps1"
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed" }

$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vsWhere) {
    $msbuild = & $vsWhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
}
if (-not $msbuild) { $msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source }
if (-not $msbuild) { throw "MSBuild.exe not found. Install Visual Studio 2026 or Build Tools for Visual Studio." }

& $msbuild "$root\FancyToolAva.Msix\FancyToolAva.Msix.wapproj" `
    /p:Configuration=$Configuration `
    /p:Platform=$Platform `
    /p:AppxPackageSigningEnabled=true `
    /p:PackageCertificateKeyFile="$root\FancyToolAva.Msix\FancyToolAva.Msix_TemporaryKey.pfx" `
    /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed" }

$msix = Get-ChildItem "$root\FancyToolAva.Msix\bin\$Platform\$Configuration\*.msix" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msix) {
    Write-Host ""
    Write-Host "MSIX generated: $($msix.FullName)" -ForegroundColor Green
    Write-Host ("  Size: {0} MB" -f [math]::Round($msix.Length / 1MB, 2))
    Write-Host ""
    Write-Host "Install steps (first time):" -ForegroundColor Cyan
    Write-Host "  1. Run PowerShell as Administrator"
    Write-Host "  2. Import-PfxCertificate -FilePath '$root\FancyToolAva.Msix\FancyToolAva.Msix_TemporaryKey.pfx' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  3. Add-AppxPackage -Path '$($msix.FullName)'"
} else {
    Write-Warning "No .msix artifact found in expected path"
}