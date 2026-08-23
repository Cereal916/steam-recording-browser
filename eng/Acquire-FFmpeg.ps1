#requires -Version 7.6
#requires -PSEdition Core

[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$AssetName = 'ffmpeg-n8.1-latest-win64-gpl-8.1.zip'
$ReleaseBase = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'
$AssetUrl = "$ReleaseBase/$AssetName"
$ChecksumsUrl = "$ReleaseBase/checksums.sha256"
$ResolvedDestination = [System.IO.Path]::GetFullPath($Destination)
$TemporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("SteamRecordingBrowser-FFmpeg-" + [Guid]::NewGuid().ToString('N'))
$Archive = Join-Path $TemporaryRoot $AssetName
$Checksums = Join-Path $TemporaryRoot 'checksums.sha256'
$Extracted = Join-Path $TemporaryRoot 'extracted'

try {
    [void](New-Item -ItemType Directory -Path $TemporaryRoot -Force)
    [void](New-Item -ItemType Directory -Path $Extracted -Force)
    Write-Host 'Downloading FFmpeg 8.1 GPL build...' -ForegroundColor Cyan
    Invoke-WebRequest -Uri $AssetUrl -OutFile $Archive
    Invoke-WebRequest -Uri $ChecksumsUrl -OutFile $Checksums

    $ExpectedLine = Get-Content -LiteralPath $Checksums | Where-Object { $_ -match [regex]::Escape($AssetName) } | Select-Object -First 1
    if (-not $ExpectedLine -or $ExpectedLine -notmatch '^([a-fA-F0-9]{64})') {
        throw "Could not find a SHA-256 entry for $AssetName"
    }

    $ExpectedHash = $Matches[1].ToLowerInvariant()
    $ActualHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualHash -ne $ExpectedHash) {
        throw "FFmpeg archive checksum mismatch. Expected $ExpectedHash; received $ActualHash"
    }

    Expand-Archive -LiteralPath $Archive -DestinationPath $Extracted
    $Ffmpeg = Get-ChildItem $Extracted -Filter 'ffmpeg.exe' -File -Recurse | Select-Object -First 1
    $Ffprobe = Get-ChildItem $Extracted -Filter 'ffprobe.exe' -File -Recurse | Select-Object -First 1
    if (-not $Ffmpeg -or -not $Ffprobe) {
        throw 'The FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe.'
    }

    $Bin = Join-Path $ResolvedDestination 'bin'
    [void](New-Item -ItemType Directory -Path $Bin -Force)
    Copy-Item -LiteralPath $Ffmpeg.FullName -Destination $Bin -Force
    Copy-Item -LiteralPath $Ffprobe.FullName -Destination $Bin -Force

    $License = Get-ChildItem $Extracted -Filter 'LICENSE.txt' -File -Recurse | Select-Object -First 1
    if ($License) { Copy-Item -LiteralPath $License.FullName -Destination (Join-Path $ResolvedDestination 'LICENSE.txt') -Force }

    $VersionLines = @(& $Ffmpeg.FullName -version 2>&1 | Select-Object -First 4)
    $VersionText = $VersionLines -join [Environment]::NewLine
    $SourceRef = 'n8.1'
    if ($VersionLines[0] -match '-g(?<commit>[a-fA-F0-9]{7,40})-') {
        $SourceRef = $Matches['commit']
    }
    $Notice = @"
FFmpeg binary distribution notice
=================================

Steam Recording Browser invokes FFmpeg as a separate, replaceable executable.
This package uses the BtbN FFmpeg 8.1 Windows x64 GPL static build. FFmpeg and
its included libraries are distributed under their respective licenses; the
GPL build is not covered by Steam Recording Browser's MIT license.

Binary archive: $AssetUrl
Binary SHA-256: $ActualHash
Build scripts and dependency source recipes: https://github.com/BtbN/FFmpeg-Builds
FFmpeg corresponding source: https://github.com/FFmpeg/FFmpeg/tree/$SourceRef
FFmpeg legal information: https://ffmpeg.org/legal.html

If any corresponding source above becomes unavailable, request the complete
corresponding source by opening an issue at
https://github.com/Cereal916/steam-recording-browser/issues. The project will
honor source requests for at least three years after distributing this binary.

$VersionText
"@
    Set-Content -LiteralPath (Join-Path $ResolvedDestination 'SOURCE-AND-LICENSE.txt') -Value $Notice -Encoding utf8NoBOM
}
finally {
    if (Test-Path -LiteralPath $TemporaryRoot) { Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force }
}
