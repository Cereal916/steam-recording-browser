# Steam Recording Browser

Steam Recording Browser is a Windows desktop application for browsing,
organizing, playing, and exporting clips created by Steam Game Recording.

Current release: **1.0.2**

## Features

- Recursively discovers Steam Game Recording `session.mpd` files
- Uses Steam's existing `thumbnail.jpg` artwork
- Resolves installed Steam game names from local Steam app manifests
- Favorites, descriptions, and tags
- Live search plus game, tag, and favorites filters
- Sorting by newest, oldest, largest, and smallest
- Metadata backup and import using stable recording identities
- Integrated WPF/libVLC playback
- Steam DASH compatibility manifest generation without modifying originals
- Timeline seeking and frame preview
- MP4 remux export through bundled libVLC
- Startup progress UI
- Self-contained Windows x64 deployment

## Technology

- C#
- WPF
- .NET 10
- LibVLCSharp.WPF
- VideoLAN libVLC
- Windows x64

The published application is self-contained and does not require a separate
.NET runtime, VLC installation, or PowerShell installation.

## Repository layout

```text
.
├── .github/                 GitHub workflows and pull-request metadata
├── .run/                    Shared JetBrains Rider run configurations
├── eng/                     Engineering and release tooling
├── docs/                    Architecture, ADRs, and development docs
├── src/
│   └── SteamRecordingBrowser/
├── tests/
│   └── SteamRecordingBrowser.Tests/
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── NuGet.Config
└── SteamRecordingBrowser.sln
```

## Development with JetBrains Rider

Open `SteamRecordingBrowser.sln` in Rider.

The repository includes a shared run/debug configuration named:

**Steam Recording Browser**

Use:

- **Shift+F10** to Run
- **Shift+F9** to Debug

The configuration builds the application before launch and targets
`src/SteamRecordingBrowser/SteamRecordingBrowser.csproj`.

See [docs/development.md](docs/development.md) for the full development
workflow.

## Command-line build

Restore:

```powershell
dotnet restore SteamRecordingBrowser.sln --configfile NuGet.Config
```

Build:

```powershell
dotnet build SteamRecordingBrowser.sln -c Debug
```

Test:

```powershell
dotnet test tests/SteamRecordingBrowser.Tests/SteamRecordingBrowser.Tests.csproj
```

## Release build

From the repository root:

```powershell
./Build Release.cmd
```

Release artifacts are written to:

```text
artifacts/publish/
```

The release packaging step produces a self-contained Windows x64 folder and ZIP
with libVLC and its plugin tree included.

## Runtime data

Application metadata:

```text
%LOCALAPPDATA%\SteamRecordingBrowser\library.json
```

Application settings:

```text
%LOCALAPPDATA%\SteamRecordingBrowser\settings.json
```

Runtime data is intentionally stored outside the repository.

## Documentation

- [Architecture](docs/architecture.md)
- [Architecture decisions](docs/adr/README.md)
- [Development guide](docs/development.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## Versioning

Starting with **1.0.0**, Steam Recording Browser follows semantic versioning:

- **MAJOR**: incompatible behavior or data-format changes
- **MINOR**: backward-compatible features
- **PATCH**: backward-compatible fixes

The earlier `30.x` numbers were internal development iterations and are
preserved only in the changelog as pre-1.0 history.

## Deployment model

libVLC is a native multimedia runtime with native DLLs and a plugin directory,
so production releases intentionally remain a portable folder/ZIP rather than
forcing the application into a single executable.

Keep all files in the published folder together.

## License

No repository license is added automatically. Add the license you want before
publishing the repository for third-party reuse.
