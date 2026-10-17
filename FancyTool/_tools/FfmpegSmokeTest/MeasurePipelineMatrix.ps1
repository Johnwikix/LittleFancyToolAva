param(
    [Parameter(Mandatory)][string]$BaselineBinary,
    [Parameter(Mandatory)][string]$CandidateBinary,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$ReuseBaseline,
    [string]$CasePattern = '*',
    [switch]$ExtendedOnly
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$env:FANCYTOOL_DATA_DIRECTORY = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$records = [Collections.Generic.List[object]]::new()
$cases = [Collections.Generic.List[object]]::new()
if (-not $ExtendedOnly) {
    foreach ($model in 'Anime4KCnnS', 'Anime4KCnnM', 'FsrcnnxS', 'FsrcnnxM', 'RavuLiteR2', 'RavuLiteR3') {
        $precisions = if ($model.StartsWith('Ravu')) { @('fp32') } else { @('fp16', 'fp32') }
        foreach ($precision in $precisions) {
            foreach ($rife in 'Rife425Lite', 'Rife425') {
                $cases.Add(@{ name = "$model-$precision-$rife"; model = $model; precision = $precision; rife = $rife;
                    scale = 2; multiplier = 4; modes = $(if ($rife -eq 'Rife425Lite') { 'sr,both,both-reverse' } else { 'rife,both,both-reverse' }); interpolation = 'Rife' })
            }
        }
    }
}
foreach ($scale in 2, 4) {
    foreach ($multiplier in 2, 6) {
        foreach ($rife in 'Rife425Lite', 'Rife425') {
            $cases.Add(@{ name = "FsrcnnxM-fp16-$rife-sr$scale-fi$multiplier"; model = 'FsrcnnxM'; precision = 'fp16'; rife = $rife;
                scale = $scale; multiplier = $multiplier; modes = 'both,both-reverse'; interpolation = 'Rife' })
        }
    }
}
foreach ($interpolation in 'MotionCompensation', 'NvidiaOpticalFlow', 'AmdOpticalFlow') {
    $cases.Add(@{ name = $interpolation; model = 'FsrcnnxM'; precision = 'fp16'; rife = 'Rife425Lite';
        scale = 2; multiplier = 2; modes = 'rife,both,both-reverse'; interpolation = $interpolation })
}
$previous = @{}
if ($ReuseBaseline -and (Test-Path -LiteralPath (Join-Path $output 'matrix.json'))) {
    foreach ($entry in (Get-Content -LiteralPath (Join-Path $output 'matrix.json') -Raw | ConvertFrom-Json)) {
        $previous[$entry.case.name] = $entry
    }
}
foreach ($case in $cases) {
    if ($case.name -notlike $CasePattern) { continue }
    $entry = [ordered]@{ case = $case; baselineExit = $null; candidateExit = $null; comparisonExit = $null }
    foreach ($side in 'baseline', 'candidate') {
        if ($side -eq 'baseline' -and $ReuseBaseline -and $previous.ContainsKey($case.name)) {
            $entry.baselineExit = $previous[$case.name].baselineExit
            continue
        }
        $directory = Join-Path $output ($case.name + '/' + $side)
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $binary = if ($side -eq 'baseline') { $BaselineBinary } else { $CandidateBinary }
        $benchArgs = @($directory, '--pipeline-benchmark', '--width', '640', '--height', '360', '--frames', '6', '--repeats', '1',
            '--modes', $case.modes, '--model', $case.model, '--sr-scale', $case.scale, '--multiplier', $case.multiplier,
            '--interpolation-model', $case.rife, '--interpolation-mode', $case.interpolation)
        if ($case.precision -eq 'fp32') { $benchArgs += '--fp32' }
        & $binary @benchArgs *> (Join-Path $directory 'run.log')
        $entry[($side + 'Exit')] = $LASTEXITCODE
        Write-Output "$($case.name) $side exit=$LASTEXITCODE"
    }
    if ($entry.baselineExit -eq 0 -and $entry.candidateExit -eq 0) {
        $directory = Join-Path $output ($case.name + '/comparison')
        & $CandidateBinary $directory --pipeline-compare --before (Join-Path $output ($case.name + '/baseline')) --after (Join-Path $output ($case.name + '/candidate')) --modes $case.modes --before-iteration 1 --after-iteration 1 *> ($directory + '.log')
        $entry.comparisonExit = $LASTEXITCODE
        Write-Output "$($case.name) comparison exit=$LASTEXITCODE"
    }
    $records.Add($entry)
    $records | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'matrix.json') -Encoding utf8
}
