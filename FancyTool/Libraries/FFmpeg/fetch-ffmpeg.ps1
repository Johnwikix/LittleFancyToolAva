#!/usr/bin/env pwsh
<#
.SYNOPSIS
    获取 FFmpeg GPL shared 库（仓库不收录这些二进制）。

.DESCRIPTION
    仓库只保留 Libraries/FFmpeg/BUILD_INFO.md 与 LICENSE.txt：FFmpeg 六库单文件超过
    GitHub 的 100 MB 限制，因此不再入库。本脚本从 BUILD_INFO.md 读取固定的 BtbN
    发布（release tag 与资产名），下载后解出应用实际依赖的六个库，并用 BUILD_INFO.md
    记录的 SHA-256 逐个校验；校验不通过即报错退出，不写入目标目录。

.PARAMETER Platform
    win / linux / all。默认按当前操作系统。

.PARAMETER ArchiveDirectory
    离线模式：改从该目录读取已下载的发布包（文件名须与 BUILD_INFO.md 记录一致），不联网。

.PARAMETER Force
    即使本地文件已存在且校验通过，也重新解包覆盖。

.PARAMETER VerifyOnly
    只校验本地已有文件的 SHA-256，不下载也不解包。

.EXAMPLE
    pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -Platform all

.EXAMPLE
    pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -VerifyOnly

.EXAMPLE
    pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -Platform linux -ArchiveDirectory D:\downloads
#>
[CmdletBinding()]
param(
    [ValidateSet('win', 'linux', 'all')]
    [string]$Platform = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win' } else { 'linux' }),

    [string]$ArchiveDirectory,

    [switch]$Force,

    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$libraryRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $libraryRoot '../../..')).Path
$buildInfoPath = Join-Path $libraryRoot 'BUILD_INFO.md'
$cacheRoot = Join-Path $repoRoot '.qa/ffmpeg-fetch'

function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# BUILD_INFO.md 是版本、资产名与 SHA-256 的唯一来源。
function Read-BuildInfo([string]$text) {
    $tag = [regex]::Match($text, 'autobuild-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}').Value
    if (-not $tag) { throw "BUILD_INFO.md 中找不到 BtbN release tag（autobuild-...）。" }

    $assetPatterns = @{
        win   = '`(ffmpeg-[^`]*?-win64-gpl-shared\.zip)`'
        linux = '`(ffmpeg-[^`]*?-linux64-gpl-shared\.tar\.xz)`'
    }
    $assets = @{}
    foreach ($key in $assetPatterns.Keys) {
        $asset = [regex]::Match($text, $assetPatterns[$key]).Groups[1].Value
        if (-not $asset) { throw "BUILD_INFO.md 中找不到 $key 的发布包名。" }
        $assets[$key] = $asset
    }

    $hashes = @{}
    $section = $null
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^###\s+(\S+)\s*$') { $section = $Matches[1]; $hashes[$section] = [ordered]@{}; continue }
        if ($line -match '^##\s') { $section = $null; continue }
        if ($section -and $line -match '^-\s+`([^`]+)`:\s*([0-9a-fA-F]{64})\s*$') {
            $hashes[$section][$Matches[1]] = $Matches[2].ToLowerInvariant()
        }
    }
    foreach ($key in @('win-x64', 'linux-x64')) {
        if (-not $hashes.Contains($key) -or $hashes[$key].Count -eq 0) { throw "BUILD_INFO.md 的 $key 小节没有 SHA-256 记录。" }
    }

    return @{ Tag = $tag; Assets = $assets; Hashes = $hashes }
}

# 目标目录里已存在的文件是否与 BUILD_INFO.md 的 SHA-256 完全一致。
function Test-LibraryDirectory([string]$directory, $expectedHashes) {
    $mismatched = @()
    foreach ($name in $expectedHashes.Keys) {
        $file = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $file)) { $mismatched += "$name (缺失)"; continue }
        if ((Get-FileSha256 $file) -ne $expectedHashes[$name]) { $mismatched += "$name (SHA-256 不符)" }
    }
    return $mismatched
}

# 发布包里同一库可能是 name 本身，也可能是指向 name.<版本> 的符号链接；
# 用哈希挑出真正的内容文件，避免 Windows 解包符号链接时的差异。
function Find-PayloadFile([string]$root, [string]$name, [string]$expectedHash) {
    $candidates = Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.Name -eq $name -or $_.Name.StartsWith("$name.") }
    foreach ($candidate in $candidates) {
        if ((Get-FileSha256 $candidate.FullName) -eq $expectedHash) { return $candidate.FullName }
    }
    $found = ($candidates | ForEach-Object { $_.Name }) -join ', '
    if (-not $found) { $found = '未找到同名文件' } else { $found = "候选 $found 的 SHA-256 都不符" }
    throw "发布包中解析 $name 失败：$found。"
}

function Get-ArchivePath([string]$key, [string]$asset, [string]$tag) {
    if ($ArchiveDirectory) {
        $local = Join-Path $ArchiveDirectory $asset
        if (-not (Test-Path -LiteralPath $local)) { throw "离线目录缺少发布包：$local" }
        return (Resolve-Path -LiteralPath $local).Path
    }

    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $cached = Join-Path $cacheRoot $asset
    if (-not (Test-Path -LiteralPath $cached)) {
        $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$tag/$asset"
        Write-Host "下载 $url"
        Invoke-WebRequest -Uri $url -OutFile $cached
    }
    return $cached
}

function Expand-ArchiveTo([string]$archive, [string]$destination) {
    if ($archive -like '*.zip') {
        Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
        return
    }
    & tar -xf $archive -C $destination
    if ($LASTEXITCODE -ne 0) { throw "tar 解包失败（退出码 $LASTEXITCODE）：$archive" }
}

$info = Read-BuildInfo (Get-Content -Raw -LiteralPath $buildInfoPath)
$requested = if ($Platform -eq 'all') { @('win', 'linux') } else { @($Platform) }

$failed = $false
foreach ($key in $requested) {
    $section = "$key-x64"
    $expected = $info.Hashes[$section]
    $destination = Join-Path $libraryRoot $section

    Write-Host "== $section ($($expected.Count) 个库) =="

    if ($VerifyOnly) {
        $problems = Test-LibraryDirectory $destination $expected
        if ($problems.Count -eq 0) {
            Write-Host "  校验通过：$destination"
        } else {
            $failed = $true
            Write-Error ("校验失败：{0} -> {1}。请运行 fetch-ffmpeg.ps1 -Platform {2} -Force 重新获取。" -f $destination, ($problems -join '; '), $key)
        }
        continue
    }

    $problems = Test-LibraryDirectory $destination $expected
    if ($problems.Count -eq 0 -and -not $Force) {
        Write-Host "  已是最新（SHA-256 与 BUILD_INFO.md 一致），跳过。"
        continue
    }

    $asset = $info.Assets[$key]
    $archive = Get-ArchivePath $key $asset $info.Tag
    $staging = Join-Path $cacheRoot ("extract-$section-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    try {
        Expand-ArchiveTo $archive $staging
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        foreach ($name in $expected.Keys) {
            $source = Find-PayloadFile $staging $name $expected[$name]
            Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name) -Force
            Write-Host ("  已解出 {0}（SHA-256 一致）" -f $name)
        }
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $problems = Test-LibraryDirectory $destination $expected
    if ($problems.Count -ne 0) {
        $failed = $true
        Write-Error ("写入后校验仍失败：{0}" -f ($problems -join '; '))
    }
}

if ($failed) { exit 1 }
Write-Host 'FFmpeg shared 库就绪。'
