param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Frames = 72,
    [int]$Multiplier = 4,
    [int]$Repeats = 3,
    [string]$Modes = 'sr,rife,both',
    [string]$Model = 'FsrcnnxM',
    [string]$InterpolationModel = 'Rife425Lite',
    [string]$InterpolationMode = 'Rife',
    [int]$SrScale = 2,
    [int]$TileIndex = 2,
    [string]$BinaryPath,
    [string]$InputFile,
    [string]$Profile,
    [switch]$Fp32,
    [switch]$SceneDetection,
    [switch]$RepeatSource,
    [string]$BaselineCommit,
    [string]$SnapshotDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$project = Join-Path $PSScriptRoot 'FfmpegSmokeTest.csproj'
$buildArgs = @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-v:q')
$binaryRoot = Join-Path $PSScriptRoot 'bin'
if ($BaselineCommit -and $SnapshotDirectory) { throw '基线提交和实验快照不能同时指定。' }

if ($BaselineCommit) {
    if ($BaselineCommit -notmatch '^[0-9a-fA-F]{7,40}$') { throw 'BaselineCommit 必须是提交 SHA。' }
    # 保持工作区源码不变；仅在本次基线构建中替换生产文件。
    $sources = Join-Path $output 'baseline-source'
    New-Item -ItemType Directory -Path $sources -Force | Out-Null
    $files = @('VideoTranscodeService.cs', 'VideoTranscodeService.SuperResolution.cs',
        'VideoTranscodeService.Interpolation.cs', 'OrtVideoSrTileModel.cs',
        'VideoFrameUpscaler.cs', 'OrtVideoFrameInterpolator.cs',
        'VideoSuperResolutionService.cs', 'VideoInterpolationService.cs')
    foreach ($file in $files) {
        $lines = & git -C $repository show "${BaselineCommit}:FancyTool/Services/$file"
        if ($LASTEXITCODE -ne 0) { throw "无法读取基线文件 $file" }
        $source = ($lines -join "`r`n") + "`r`n"
        if ($file -eq 'VideoTranscodeService.cs') {
            $marker = '            if (encodingProgress != null) progress = encodingProgress;'
            if (-not $source.Contains($marker)) { throw '基线计时插入点不匹配。' }
            $source = $source.Replace($marker, $marker + "`r`n            using var metrics = VideoPipelineMetrics.Begin();")
        }
        [IO.File]::WriteAllText((Join-Path $sources $file), $source, [Text.UTF8Encoding]::new($false))
    }
    $remove = ($files | ForEach-Object { "Services/$_" }) -join ';'
    $remove += ';Services/VideoTranscodeService.Pipeline.cs'
    $escapedSources = [Security.SecurityElement]::Escape($sources)
    $targets = @"
<Project>
    <ItemGroup Condition="'`$(MSBuildProjectName)' == 'FancyToolAva'">
      <Compile Remove="$remove" />
      <Compile Include="$escapedSources/*.cs" />
    </ItemGroup>
</Project>
"@
    $targetPath = Join-Path $output 'baseline.targets'
    [IO.File]::WriteAllText($targetPath, $targets, [Text.UTF8Encoding]::new($false))
    $binaryRoot = Join-Path $output 'bin'
    $buildArgs += @("-p:CustomBeforeMicrosoftCommonTargets=$targetPath", "-p:BaseOutputPath=$binaryRoot/")
}
if ($SnapshotDirectory) {
    $snapshot = [IO.Path]::GetFullPath($SnapshotDirectory)
    $copied = Join-Path $output 'snapshot-source'
    $groups = [Collections.Generic.List[string]]::new()
    foreach ($section in 'Services', 'Smoke') {
        $directory = Join-Path $snapshot $section
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        $target = Join-Path $copied $section
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $names = [Collections.Generic.List[string]]::new()
        foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.cs.txt') {
            $name = $file.Name.Substring(0, $file.Name.Length - 4)
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $name) -Force
            $names.Add($(if ($section -eq 'Services') { "Services/$name" } else { $name }))
        }
        $projectName = if ($section -eq 'Services') { 'FancyToolAva' } else { 'FfmpegSmokeTest' }
        $remove = [Security.SecurityElement]::Escape(($names -join ';'))
        $include = [Security.SecurityElement]::Escape((Join-Path $target '*.cs'))
        $groups.Add("<ItemGroup Condition=`"'`$(MSBuildProjectName)' == '$projectName'`"><Compile Remove=`"$remove`"/><Compile Include=`"$include`"/></ItemGroup>")
    }
    $targetPath = Join-Path $output 'snapshot.targets'
    [IO.File]::WriteAllText($targetPath, '<Project>' + ($groups -join "`r`n") + '</Project>', [Text.UTF8Encoding]::new($false))
    $binaryRoot = Join-Path $output 'bin'
    $buildArgs += @("-p:CustomBeforeMicrosoftCommonTargets=$targetPath", "-p:BaseOutputPath=$binaryRoot/")
}
if (-not $SkipBuild) {
    & dotnet @buildArgs *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log') -Tail 20; exit $LASTEXITCODE }
}
$binary = Join-Path $binaryRoot 'Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
if ($BinaryPath) { $binary = [IO.Path]::GetFullPath($BinaryPath) }
$previousData = $env:FANCYTOOL_DATA_DIRECTORY
$env:FANCYTOOL_DATA_DIRECTORY = $data
$telemetry = Start-Process -FilePath 'nvidia-smi.exe' -ArgumentList @(
    '--query-gpu=timestamp,utilization.gpu,utilization.memory,power.draw,memory.used,clocks.sm,clocks.mem',
    '--format=csv', '-lms', '200') -RedirectStandardOutput (Join-Path $output 'gpu.csv') -PassThru -WindowStyle Hidden
try {
    $benchArgs = @($output, '--pipeline-benchmark', '--width', $Width, '--height', $Height,
        '--frames', $Frames, '--multiplier', $Multiplier, '--modes', $Modes, '--repeats', $Repeats,
        '--model', $Model, '--interpolation-model', $InterpolationModel, '--interpolation-mode', $InterpolationMode,
        '--sr-scale', $SrScale, '--tile-index', $TileIndex)
    if ($InputFile) { $benchArgs += @('--input', [IO.Path]::GetFullPath($InputFile)) }
    if ($Profile) { $benchArgs += @('--profile', $Profile) }
    if ($Fp32) { $benchArgs += '--fp32' }
    if ($RepeatSource) { $benchArgs += '--repeat-source' }
    if ($SceneDetection) { $benchArgs += '--scene-detection' }
    & $binary @benchArgs *> (Join-Path $output 'run.log')
    $result = $LASTEXITCODE
}
finally {
    Stop-Process -Id $telemetry.Id -ErrorAction SilentlyContinue
    $env:FANCYTOOL_DATA_DIRECTORY = $previousData
}
Get-Content (Join-Path $output 'run.log') -Tail 8
exit $result
