# Development

## Prerequisites

- Windows x64
- .NET 10 SDK
- JetBrains Rider 2026.x or another .NET 10 capable IDE

libVLC is restored from NuGet; a separate VLC installation is not required for
development or end-user deployment.

## JetBrains Rider

Open:

```text
SteamRecordingBrowser.sln
```

The repository includes the shared run configuration:

```text
Steam Recording Browser
```

It targets:

```text
src/SteamRecordingBrowser/SteamRecordingBrowser.csproj
```

and builds before launch.

Common Rider shortcuts:

- Run: `Shift+F10`
- Debug: `Shift+F9`

## Command line

Restore:

```powershell
dotnet restore SteamRecordingBrowser.sln --configfile NuGet.Config
```

Build:

```powershell
dotnet build SteamRecordingBrowser.sln -c Debug
```

Run tests:

```powershell
dotnet test tests/SteamRecordingBrowser.Tests/SteamRecordingBrowser.Tests.csproj
```

## Release packaging

From the repository root:

```powershell
./Build Release.cmd
```

The wrapper invokes the source-controlled engineering script:

```text
eng/Build-Release.ps1
```

The release script:

1. reads the semantic version from the application `.csproj`;
2. restores dependencies;
3. publishes Windows x64 as self-contained;
4. validates the application executable and libVLC plugin tree;
5. creates a portable release ZIP under `artifacts/publish/`.

Release naming:

```text
SteamRecordingBrowser-<version>-win-x64/
SteamRecordingBrowser-<version>-win-x64.zip
```

## Versioning

The project follows semantic versioning beginning with `1.0.0`.

Update the following together for each release:

- `<Version>`
- `<AssemblyVersion>`
- `<FileVersion>`
- user-visible application version
- `CHANGELOG.md`

The release build script reads the version from the project file and should not
contain a separately maintained application version.

## Local runtime data

The application stores runtime settings and metadata under:

```text
%LOCALAPPDATA%\SteamRecordingBrowser
```

Do not commit:

- recordings
- generated compatibility manifests
- local metadata/settings
- logs
- IDE-specific local state
- build or publish output
