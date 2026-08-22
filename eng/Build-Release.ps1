#requires -Version 7.6
#requires -PSEdition Core

$ErrorActionPreference = 'Stop'

$BuildRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent $BuildRoot
$Project = Join-Path $RepoRoot 'src\SteamRecordingBrowser\SteamRecordingBrowser.csproj'
$BuildProps = Join-Path $RepoRoot 'Directory.Build.props'
$License = Join-Path $RepoRoot 'LICENSE'
$ThirdPartyNotices = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'
$PublishRoot = Join-Path $RepoRoot 'artifacts\publish'

[xml]$BuildPropsXml = Get-Content -Path $BuildProps -Raw
$Version = [string]($BuildPropsXml.Project.PropertyGroup.VersionPrefix | Select-Object -First 1)

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not read <VersionPrefix> from $BuildProps"
}

$AppFolder = Join-Path $PublishRoot "SteamRecordingBrowser-$Version-win-x64"
$ZipPath = Join-Path $PublishRoot "SteamRecordingBrowser-$Version-win-x64.zip"

Write-Host ''
Write-Host "Building Steam Recording Browser $Version..." -ForegroundColor Cyan
Write-Host 'C# WPF / .NET 10 / bundled libVLC' -ForegroundColor DarkGray
Write-Host ''

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'The .NET 10 SDK is required to build the application.'
}

$sdks = @(& $dotnet.Source --list-sdks)
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'Install the .NET 10 SDK (Microsoft.DotNet.SDK.10) and run the build again.'
    }

    Write-Host 'Installing .NET 10 SDK...' -ForegroundColor Yellow
    & $winget.Source install --id Microsoft.DotNet.SDK.10 --exact --source winget `
        --accept-package-agreements --accept-source-agreements --silent

    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install .NET 10 SDK. Exit code: $LASTEXITCODE"
    }
}

if (Test-Path $PublishRoot) {
    Remove-Item $PublishRoot -Recurse -Force
}
[void](New-Item -ItemType Directory -Path $AppFolder -Force)

# Isolate restore from any machine-level NuGet source mapping.
$nuget = Join-Path $RepoRoot 'NuGet.Config'
if (-not (Test-Path $nuget)) {
    throw "NuGet.Config is missing: $nuget"
}

& $dotnet.Source restore $Project --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $LASTEXITCODE" }

& $dotnet.Source publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $AppFolder

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

$exe = Join-Path $AppFolder 'Steam Recording Browser.exe'
if (-not (Test-Path $exe)) {
    throw "Expected executable was not produced: $exe"
}

# Sanity-check that native libVLC was actually bundled.
$libvlc = Get-ChildItem $AppFolder -Filter 'libvlc.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$plugins = Get-ChildItem $AppFolder -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object Name -eq 'plugins' | Select-Object -First 1

if (-not $libvlc) {
    Write-Warning 'libvlc.dll was not found in the publish output. Check VideoLAN.LibVLC.Windows package contents.'
}
if (-not $plugins) {
    Write-Warning 'A libVLC plugins folder was not found in the publish output.'
}

Copy-Item -LiteralPath $License -Destination $AppFolder
Copy-Item -LiteralPath $ThirdPartyNotices -Destination $AppFolder

Compress-Archive -Path (Join-Path $AppFolder '*') -DestinationPath $ZipPath -CompressionLevel Optimal

$checksum = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$ZipPath.sha256"
Set-Content -LiteralPath $checksumPath `
    -Value "$checksum  $(Split-Path -Leaf $ZipPath)" `
    -Encoding utf8NoBOM

Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
Write-Host "Application folder:"
Write-Host "  $AppFolder"
Write-Host ''
Write-Host "Portable ZIP:"
Write-Host "  $ZipPath"
Write-Host "SHA-256 checksum:"
Write-Host "  $checksumPath"
Write-Host ''
Write-Host 'The published app requires no separate .NET, PowerShell, or VLC installation.'
Write-Host 'Keep the published files together; libVLC uses native DLLs and plugins from the app folder.'
