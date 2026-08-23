# Steam Recording Browser

Steam Recording Browser is a Windows desktop application for browsing,
organizing, playing, and exporting clips created by Steam Game Recording.

See the [changelog](CHANGELOG.md) for the current release and version history.

![Steam Recording Browser library](https://github.com/user-attachments/assets/6866bbac-4ec6-472a-b578-8783e908626a)

## Download and install

Steam Recording Browser supports 64-bit Windows 10 and Windows 11.

1. Download the latest `SteamRecordingBrowser-*-win-x64.zip` from
   [GitHub Releases](https://github.com/Cereal916/steam-recording-browser/releases/latest).
2. Optionally verify the ZIP using the accompanying `.sha256` file.
3. Extract the complete ZIP to a folder you control.
4. Run `Steam Recording Browser.exe`. Keep the extracted files together because
   libVLC loads its native libraries and plugins from this folder.

Releases are not currently code-signed. Windows may display an **Unknown
publisher** or Microsoft Defender SmartScreen warning. Only download releases
from this repository, and verify the published checksum when possible.

On first launch, the app automatically looks for Steam Game Recordings. If it
cannot find them, open **Settings**, select **Recording folder**, and choose the
folder containing your Steam recording sessions.

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
- H.264, HEVC, and AV1 transcoding through bundled FFmpeg
- Automatic NVENC, Quick Sync, and AMF encoding with software fallback
- Startup progress UI
- Self-contained Windows x64 deployment
- Live diagnostic log viewer with pause, search, severity toggles, and bounded history

## Technology

- C#
- WPF
- .NET 10
- LibVLCSharp.WPF
- VideoLAN libVLC
- FFmpeg 8.1 (separate bundled GPL executable for transcoding)
- Windows x64

The published application is self-contained and does not require a separate
.NET runtime, VLC installation, FFmpeg installation, or PowerShell installation.

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

## Troubleshooting

- **No recordings appear:** confirm the recording folder in **Settings**, then
  select **Refresh**.
- **Playback fails:** keep every file from the release ZIP together and confirm
  the `libvlc` directory is still present.
- **Windows blocks startup:** confirm the ZIP came from this repository and
  verify its SHA-256 checksum before allowing the unsigned application.
- **Need diagnostic information:** use **Open log** in the main window to watch,
  pause, search, or filter live entries, or open the raw file from the viewer.
  When filing a bug, remove any paths or other personal information you do not
  want to share.

For unresolved problems, open a
[bug report](https://github.com/Cereal916/steam-recording-browser/issues/new/choose).

## Documentation

- [Architecture](docs/architecture.md)
- [Architecture decisions](docs/adr/README.md)
- [Development guide](docs/development.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Support](SUPPORT.md)
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

Steam Recording Browser is available under the [MIT License](LICENSE).
Bundled dependencies retain their own licenses; see
[Third-Party Notices](THIRD-PARTY-NOTICES.md).
