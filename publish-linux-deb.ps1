# Cross-publish from Windows to Linux x64 (self-contained, no AOT) and
# create a Debian (.deb) package via the CreateDeb MSBuild target provided
# by the Packaging.Targets NuGet package (dotnet-packaging).
#
# Note: the dotnet-deb CLI tool wrapper is NOT used here — it bundles an old
# MSBuildLocator that resolves the wrong SDK and fails on net10.0. Invoking
# the target through the pinned SDK (global.json -> 10.0.302) avoids that.
#
# Packaging metadata lives in FancyToolAva.csproj: PackagePrefix,
# PackageVersion, DebSection, DebPriority, DebDependency, AppHost and the
# Content/LinuxPath items for the .desktop file and icon.

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "FancyToolAva.csproj"

dotnet restore $csproj -r linux-x64 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet msbuild $csproj /t:CreateDeb `
    /p:Configuration=$Configuration `
    /p:RuntimeIdentifier=linux-x64 `
    /p:SelfContained=true `
    /p:PackageDir="$OutputDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild /t:CreateDeb failed" }

$deb = Get-ChildItem $OutputDir -Filter *.deb | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $deb) { throw "No .deb produced in $OutputDir" }
Write-Host "Done: $($deb.FullName)"
