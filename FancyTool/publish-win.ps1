# Publish Windows x64 Release (self-contained NativeAOT; FFmpeg.AutoGen binds via static P/Invoke to shared DLLs shipped with the app)
# The fancy-tool CLI is published beside the GUI under <output>\cli so the MSIX
# execution alias and the installer PATH entry point at a self-contained tool.
param(
    [string]$Configuration = "Release",
    [string]$Output,
    [switch]$SkipCli
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$publishArgs = @(
    "publish", "$root\FancyToolAva.csproj",
    "-c", $Configuration,
    "-f", "net10.0-windows10.0.19041.0",
    "-r", "win-x64",
    "-p:PublishSelfContained=true"
)

if ($Output) { $publishArgs += "-o", $Output }

& dotnet $publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $SkipCli) {
    $cliArgs = @(
        "publish", "$root\FancyTool.Cli\FancyTool.Cli.csproj",
        "-c", $Configuration,
        "-f", "net10.0-windows10.0.19041.0",
        "-r", "win-x64",
        "-p:PublishSelfContained=true",
        "-p:PublishAot=false",
        "-p:FancyToolMsixBuild=false"
    )
    if ($Output) {
        $cliDir = Join-Path $Output "cli"
        $cliArgs += "-o", $cliDir
    }
    & dotnet $cliArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (CLI) failed" }
}