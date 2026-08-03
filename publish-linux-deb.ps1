# Cross-publish from Windows to Linux x64 (self-contained, no AOT) and
# assemble a Debian (.deb) package directly on Windows (no dpkg needed).

param(
    [string]$Configuration = "Release",
    [string]$PackageId = "little-fancy-tool",
    [string]$PackageVersion = "1.0.0",
    [string]$Author = "LittleFancyTool",
    [string]$Description = "A desktop utility toolbox built on Avalonia",
    [string]$IconPath = "$PSScriptRoot\Assets\storeIcon.png",
    [string]$OutputDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"

function Get-Octal {
    param([int64]$Value, [int]$Width)
    $s = [Convert]::ToString($Value, 8).PadLeft($Width, [char]'0')
    $s.Substring($s.Length - $Width)
}

function Add-TarEntry {
    param(
        [System.IO.Stream]$Stream,
        [string]$Name,
        [byte]$Type,                  # 0 = file, 2 = symlink, 5 = dir
        [byte[]]$Data = $null,
        [string]$LinkName = "",
        [int]$Mode = 420,
        [int64]$Mtime = -1
    )

    if ($Mtime -lt 0) { $Mtime = [int64][Math]::Floor((Get-Date -Date (Get-Date).ToUniversalTime()).ToFileTimeUtc() / 10000000 - 11644473600) }

    $enc = [System.Text.Encoding]::ASCII
    $nameBytes = $enc.GetBytes($Name)

    $size = 0
    if ($Type -eq 0x30 -and $null -ne $Data) { $size = $Data.Length }

    $h = New-Object byte[] 512
    [Array]::Copy($nameBytes, 0, $h, 0, [Math]::Min($nameBytes.Length, 100))

    [Array]::Copy($enc.GetBytes((Get-Octal -Value $Mode -Width 8)), 0, $h, 100, 8)
    [Array]::Copy($enc.GetBytes((Get-Octal -Value 0 -Width 8)), 0, $h, 108, 8)     # uid
    [Array]::Copy($enc.GetBytes((Get-Octal -Value 0 -Width 8)), 0, $h, 116, 8)     # gid
    [Array]::Copy($enc.GetBytes((Get-Octal -Value $size -Width 12)), 0, $h, 124, 12)
    [Array]::Copy($enc.GetBytes((Get-Octal -Value $Mtime -Width 12)), 0, $h, 136, 12)

    for ($i = 148; $i -lt 156; $i++) { $h[$i] = 0x20 }   # chksum = spaces for now

    $h[156] = $Type
    if ($Type -eq 0x32) {
        $linkBytes = $enc.GetBytes($LinkName)
        [Array]::Copy($linkBytes, 0, $h, 157, [Math]::Min($linkBytes.Length, 100))
    }

    $magic = $enc.GetBytes("ustar")
    [Array]::Copy($magic, 0, $h, 257, 5)      # "ustar"
    $h[262] = 0x00                           # NUL
    $h[263] = 0x30; $h[264] = 0x30           # "00"

    $sum = 0
    foreach ($b in $h) { $sum += $b }
    $checksumBytes = $enc.GetBytes((Get-Octal -Value $sum -Width 6) + [char]0 + [char]32)
    [Array]::Copy($checksumBytes, 0, $h, 148, 8)

    $Stream.Write($h, 0, 512)
    if ($size -gt 0) {
        $Stream.Write($Data, 0, $size)
        if ($size % 512 -ne 0) {
            $zero = New-Object byte[] (512 - ($size % 512))
            $Stream.Write($zero, 0, $zero.Length)
        }
    }
}

function Build-TarGz {
    param([object[]]$Entries)
    $raw = New-Object System.IO.MemoryStream
    foreach ($e in $Entries) {
        Add-TarEntry -Stream $raw -Name $e.Name -Type $e.Type -Data $e.Data -LinkName $e.LinkName -Mode $e.Mode
    }
    $zero = New-Object byte[] 1024
    $raw.Write($zero, 0, 1024)

    $out = New-Object System.IO.MemoryStream
    $raw.Position = 0
    $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
    $raw.CopyTo($gz)
    $gz.Close()
    $out.ToArray()
}

function Add-DebMember {
    param([System.IO.Stream]$Stream, [string]$MemberName, [byte[]]$Data, [int64]$Mtime)
    $enc = [System.Text.Encoding]::ASCII

    $nameBytes = $enc.GetBytes($MemberName)
    $nameField = New-Object byte[] 16
    for ($i = 0; $i -lt 16; $i++) { $nameField[$i] = 0x20 }
    [Array]::Copy($nameBytes, 0, $nameField, 0, [Math]::Min($nameBytes.Length, 16))
    $Stream.Write($nameField, 0, 16)

    $Stream.Write($enc.GetBytes((Get-Octal -Value $Mtime -Width 12)), 0, 12)
    $Stream.Write($enc.GetBytes("0     "), 0, 6)     # uid
    $Stream.Write($enc.GetBytes("0     "), 0, 6)     # gid
    $Stream.Write($enc.GetBytes("100644  "), 0, 8)   # mode
    $Stream.Write($enc.GetBytes($Data.Length.ToString().PadLeft(10, [char]'0')), 0, 10)
    $Stream.Write([byte[]]@(0x60, 0x0A), 0, 2)        # backtick + newline

    $Stream.Write($Data, 0, $Data.Length)
    if ($Data.Length % 2 -ne 0) { $Stream.WriteByte(0x0A) }
}

function New-DebPackage {
    param(
        [string]$PublishDir,
        [string]$OutputPath,
        [string]$PackageId,
        [string]$Version,
        [string]$Author,
        [string]$Description,
        [string]$IconPath
    )

    $enc = [System.Text.Encoding]::ASCII
    $exe = "LittleFancyToolAva"
    $libDir = "usr/lib/$PackageId"

    $entries = New-Object System.Collections.Generic.List[object]
    $dirs = @("usr", "usr/lib", $libDir, "usr/bin", "usr/share", "usr/share/applications",
              "usr/share/icons", "usr/share/icons/hicolor",
              "usr/share/icons/hicolor/128x128", "usr/share/icons/hicolor/128x128/apps",
              "usr/share/doc", "usr/share/doc/$PackageId")
    foreach ($d in $dirs) {
        $entries.Add(@{ Name = $d; Type = [byte]0x35; Mode = 493; Data = $null; LinkName = "" })
    }

    $desktop = "[Desktop Entry]`nType=Application`nName=$PackageId`nComment=$Description`nExec=/usr/bin/$PackageId`nIcon=$PackageId`nTerminal=false`nCategories=Utility;System;`n"
    $entries.Add(@{ Name = "usr/share/applications/$PackageId.desktop"; Type = [byte]0x30; Mode = 420; Data = $enc.GetBytes($desktop); LinkName = "" })

    if (Test-Path $IconPath) {
        $entries.Add(@{ Name = "usr/share/icons/hicolor/128x128/apps/$PackageId.png"; Type = [byte]0x30; Mode = 420; Data = [System.IO.File]::ReadAllBytes((Resolve-Path $IconPath)); LinkName = "" })
    }

    $copyright = "Copyright for $PackageId package."
    $entries.Add(@{ Name = "usr/share/doc/$PackageId/copyright"; Type = [byte]0x30; Mode = 420; Data = $enc.GetBytes($copyright); LinkName = "" })

    $entries.Add(@{ Name = "usr/bin/$PackageId"; Type = [byte]0x32; Mode = 511; Data = $null; LinkName = "/$libDir/$exe" })

    $publishRoot = (Resolve-Path $PublishDir).Path
    $toolFiles = Get-ChildItem -Path $publishRoot -Recurse -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Extension -ne '.deps.json' }
    $totalSize = [int64]0
    foreach ($f in $toolFiles) {
        $rel = $f.FullName.Substring($publishRoot.Length).TrimStart('\', '/')
        $rel = $rel.Replace('\', '/')
        $mode = if ($rel -eq $exe) { 493 } else { 420 }
        $data = [System.IO.File]::ReadAllBytes($f.FullName)
        $totalSize += $data.Length
        $entries.Add(@{ Name = "$libDir/$rel"; Type = [byte]0x30; Mode = $mode; Data = $data; LinkName = "" })
    }

    $dataTar = Build-TarGz -Entries $entries.ToArray()

    $installedKb = [math]::Floor($totalSize / 1024)
    $control = @"
Package: $PackageId
Version: $Version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: $Author
Installed-Size: $installedKb
Depends: libc6 (>= 2.30), libx11-6, libxcb1, libx11-xcb1, libxcursor1, libxrandr2, libxi6, libxext6, libxtst6, libgl1, libfontconfig1
Description: $Description
 A desktop utility toolbox (communication, encryption, hashing, encoding, file and image tools).
"@
    $controlTar = Build-TarGz -Entries @(
        @{ Name = "./control"; Type = [byte]0x30; Mode = 420; Data = $enc.GetBytes($control); LinkName = "" }
    )

    $now = [int64][Math]::Floor((Get-Date -Date (Get-Date).ToUniversalTime()).ToFileTimeUtc() / 10000000 - 11644473600)
    $deb = [System.IO.File]::Create($OutputPath)
    $deb.Write($enc.GetBytes("!<arch>`n"), 0, 8)
    Add-DebMember -Stream $deb -MemberName "debian-binary" -Data $enc.GetBytes("2.0`n") -Mtime $now
    Add-DebMember -Stream $deb -MemberName "control.tar.gz" -Data $controlTar -Mtime $now
    Add-DebMember -Stream $deb -MemberName "data.tar.gz" -Data $dataTar -Mtime $now
    $deb.Close()
}

$root = $PSScriptRoot
$publishOut = Join-Path $PSScriptRoot "out-linux-x64"

dotnet publish "$root\LittleFancyToolAva.csproj" `
    -c $Configuration `
    -f net10.0 `
    -r linux-x64 `
    -p:PublishSelfContained=true `
    -p:PublishAot=false `
    -o $publishOut

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$outputPath = Join-Path $OutputDir "${PackageId}_${PackageVersion}_amd64.deb"
New-DebPackage -PublishDir $publishOut -OutputPath $outputPath -PackageId $PackageId `
    -Version $PackageVersion -Author $Author -Description $Description -IconPath $IconPath

Write-Host "Done: $outputPath"