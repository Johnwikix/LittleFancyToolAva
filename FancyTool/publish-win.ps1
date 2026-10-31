# Publish Windows x64 Release (self-contained NativeAOT; FFmpeg.AutoGen binds via static P/Invoke to shared DLLs shipped with the app)
param(
    [string]$Configuration = "Release",
    [string]$Output
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