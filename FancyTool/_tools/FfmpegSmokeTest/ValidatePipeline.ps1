param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $SkipBuild) {
    & dotnet build (Join-Path $PSScriptRoot 'FfmpegSmokeTest.csproj') -c Release --no-restore --disable-build-servers -v:q *> (Join-Path $output 'build.txt')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.txt') -Tail 20; exit $LASTEXITCODE }
}
$binary = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
$previousData = $env:FANCYTOOL_DATA_DIRECTORY
$previousAvx = $env:COMPlus_EnableAVX2
$previousIntrinsic = $env:COMPlus_EnableHWIntrinsic
$env:FANCYTOOL_DATA_DIRECTORY = [IO.Path]::GetFullPath($DataDirectory)
$records = [Collections.Generic.List[object]]::new()
try {
    $cases = @(
        @{ name = 'cpu'; args = @('--pipeline-cpu') },
        @{ name = 'cpu-sse'; args = @('--pipeline-cpu') },
        @{ name = 'cpu-scalar'; args = @('--pipeline-cpu') },
        @{ name = 'adapters'; args = @('--pipeline-adapters') },
        @{ name = 'cuda-adapters'; args = @('--pipeline-cuda') },
        @{ name = 'cuda-integration'; args = @('--pipeline-cuda-integration') },
        @{ name = 'cuda-fallback'; args = @('--pipeline-cuda-fallback') },
        @{ name = 'quantizer'; args = @('--pipeline-pixel-stages') },
        @{ name = 'integration'; args = @('--pipeline-integration') },
        @{ name = 'ffmpeg'; args = @() },
        @{ name = 'sr'; args = @('--sr-only', '--sr-model', 'FsrcnnxM') }
    )
    foreach ($case in $cases) {
        $env:COMPlus_EnableAVX2 = if ($case.name -eq 'cpu-sse') { '0' } else { $previousAvx }
        $env:COMPlus_EnableHWIntrinsic = if ($case.name -eq 'cpu-scalar') { '0' } else { $previousIntrinsic }
        $directory = Join-Path $output $case.name
        $log = Join-Path $output ($case.name + '.txt')
        $arguments = @($directory) + $case.args
        $started = [DateTimeOffset]::UtcNow
        & $binary @arguments *> $log
        $records.Add(@{ name = $case.name; exitCode = $LASTEXITCODE; startedUtc = $started; endedUtc = [DateTimeOffset]::UtcNow; arguments = $arguments })
        $records | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'validation.json') -Encoding utf8
        Write-Output "$($case.name): exit=$LASTEXITCODE"
        Get-Content -LiteralPath $log -Tail 2
    }
}
finally {
    $env:FANCYTOOL_DATA_DIRECTORY = $previousData
    $env:COMPlus_EnableAVX2 = $previousAvx
    $env:COMPlus_EnableHWIntrinsic = $previousIntrinsic
}
if ($records | Where-Object { $_.exitCode -ne 0 }) { exit 1 }
exit 0
