# Publish the Windows x64 build (via publish-win.ps1) and wrap it into an
# Inno Setup installer (.exe) placed in dist\, mirroring publish-linux-deb.ps1.
#
# The installer definition lives in packaging\fancy-tool-win.iss. Its {#MyApp*}
# values (version, publish dir, output dir, icon, license) are injected here with
# ISPP /D switches, so the .iss never hard-codes a machine-specific path.
#
# Version comes from <VersionPrefix> in FancyToolAva.csproj.
# Install Inno Setup with: winget install --id JRSoftware.InnoSetup

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\dist",
    [string]$IsccPath
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "FancyToolAva.csproj"
$iss = Join-Path $root "packaging\fancy-tool-win.iss"

# The payload is always a fresh publish into a wiped staging directory. Neither an
# existing folder is safe to pack: dotnet publish never deletes files left over from
# earlier builds, and bin\<Config>\...\publish may hold output from other settings.
$stage = Join-Path $OutputDir "stage\win-x64"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

& "$root\publish-win.ps1" -Configuration $Configuration -Output $stage
if (-not (Test-Path (Join-Path $stage "FancyToolAva.exe"))) {
    throw "publish-win.ps1 produced no FancyToolAva.exe in $stage"
}

# Inno Setup is not on PATH after a winget user-scope install; check the usual roots.
if (-not $IsccPath) {
    $found = Get-Command iscc.exe -ErrorAction SilentlyContinue
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = if ($found) { $found.Source } else { $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1 }
}
if (-not $IsccPath) { throw "ISCC.exe not found. Install Inno Setup or pass -IsccPath." }

$version = [regex]::Match((Get-Content $csproj -Raw), "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
if (-not $version) { throw "No <VersionPrefix> in $csproj" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

& $IsccPath `
    "/DMyAppVersion=$version" `
    "/DMyAppPublishDir=$stage" `
    "/DMyAppOutputDir=$($OutputDir.TrimEnd('\'))" `
    "/DMyAppOutputBaseFilename=fancy-tool.$version.win-x64" `
    "/DMyAppIconFile=$(Join-Path $root 'Assets\storeIcon.ico')" `
    "/DMyAppLicenseFile=$(Join-Path (Split-Path $root -Parent) 'LICENSE')" `
    $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed" }

$exe = Get-Item (Join-Path $OutputDir "fancy-tool.$version.win-x64.exe")
Write-Host ("Done: {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB))
