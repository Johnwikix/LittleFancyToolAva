# Publish Windows x64 Release (NativeAOT enabled)
param(
    [string]$Configuration = "Release",
    [string]$Output
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$publishArgs = @(
    "publish", "$root\LittleFancyToolAva.csproj",
    "-c", $Configuration,
    "-f", "net10.0-windows",
    "-r", "win-x64",
    "-p:PublishSelfContained=true"
)

if ($Output) { $publishArgs += "-o", $Output }

& dotnet $publishArgs