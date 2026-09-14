# Development

## Prerequisites

- Windows x64
- .NET 10 SDK
- JetBrains Rider 2026.x or another .NET 10 capable IDE

libVLC is restored from NuGet; a separate VLC installation is not required for
development or end-user deployment.

Transcoded exports use FFmpeg. Release packaging downloads and verifies the
approved build automatically. To test transcoding from Rider, acquire the same
tools into the ignored local tool directory:

```powershell
pwsh ./eng/Acquire-FFmpeg.ps1 -Destination ./.tools/ffmpeg
```

The project copies that folder into build output without committing binaries.
Alternatively, developers may put `ffmpeg.exe` and `ffprobe.exe` on `PATH`.

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
dotnet test --project tests/SteamRecordingBrowser.Tests/SteamRecordingBrowser.Tests.csproj
```

## UI and UX testing without taking over the desktop

The existing xUnit suite includes real WPF dialog tests under
`tests/SteamRecordingBrowser.Tests/Ux/`. Use this harness as the default for UI
changes. It starts each scenario in a child test process assigned to a private,
undisplayed Windows desktop before WPF initializes. It never switches the user's
input desktop or sends global mouse/keyboard input. Isolation failures fail the
test before a dialog opens; there is no fallback to the interactive desktop.

Run the full suite normally, or run only UX tests after a Release build:

```powershell
dotnet build SteamRecordingBrowser.sln -c Release
dotnet tests/SteamRecordingBrowser.Tests/bin/Release/net10.0-windows/SteamRecordingBrowser.Tests.dll -trait Category=UX
```

The tests exercise actual XAML controls, focus, popup visibility, text changes,
routed keyboard/mouse events, button automation peers, save/cancel, and layout
bounds. The tag editor scenarios cover suggestions on initial focus, filtering
and clearing, keyboard and mouse selection, duplicate prevention, pending text
on save, empty tags, and long labels with scrolling. Tests use sample data and
load the application's shared styles without starting Steam scanning or libVLC.

Clip scrolling scenarios load the main window's actual list, tile, and table
XAML with sample recordings and application service handlers removed. They
check wheel movement, partial-card scrolling, virtualization, paging, horizontal
table scrolling, and top/bottom limits at normal and minimum window sizes.

Player sizing scenarios load the actual player XAML with a 16:9 placeholder
instead of libVLC. They check opening sizes with optional panels, wrapping
descriptions and tags, and smaller monitor work areas. Native playback and
physical monitor/DPI transitions still require separate manual verification.

`IsolatedWpfTest.Render` generates PNGs directly from WPF visuals; it does not
capture the screen. Dialog content and popup content are rendered separately.
Review these images alongside layout assertions to evaluate appearance. The
scale cases generate output at 100%, 150%, and 200% rendering resolution; they
do not change Windows display settings or simulate moving across monitors.

Images and individual child test reports are written to:

```text
tests/SteamRecordingBrowser.Tests/bin/Release/net10.0-windows/TestResults/ux/
```

Set `SRB_UX_ARTIFACTS` to an absolute directory to choose another location. Each
scenario runs on a fresh STA dispatcher with a bounded timeout; child processes
and their desktops are cleaned up after execution. Keep `preEnumerateTheories`
enabled in `xunit.runner.json`, so the child runner can select one parameterized
scenario by its xUnit case ID. UX tests run in the normal Windows CI test step;
images and child reports are retained as the `ux-test-results` artifact for
14 days, including on failure.

For new dialogs, use `IsolatedWpfTest.Run`, construct the dialog inside its
callback, and call `host.ShowDialog(dialog, () => { ... })` to inspect and operate
loaded controls. Use `Pump()` after asynchronous UI work, `Key()` for routed key
events, and `Click()` for button invocation. The host closes the dialog even
when an assertion fails. Verify that Save or Cancel actually closed the dialog
inside the callback, before the host's cleanup runs.

These tests validate control behavior and rendered layout. They do not replace
human usability evaluation, real screen-reader testing, physical input/IME
testing, native title-bar/DPI checks, or libVLC video-composition checks. Perform
those checks in a separate VM or an explicitly authorized interactive session,
not on the user's active desktop by default.

Implementation references: [Windows desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops),
[process desktop assignment](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow),
and [WPF visual rendering](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.rendertargetbitmap).

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

1. reads the semantic version from `Directory.Build.props`;
2. restores dependencies;
3. publishes Windows x64 as self-contained;
4. validates the application executable and libVLC plugin tree;
5. downloads and checksum-verifies the approved FFmpeg GPL build;
6. bundles FFmpeg's license and corresponding-source notice;
7. copies project and third-party notices into the application folder;
8. creates a portable release ZIP and SHA-256 checksum under
   `artifacts/publish/`.

Release naming:

```text
SteamRecordingBrowser-<version>-win-x64/
SteamRecordingBrowser-<version>-win-x64.zip
```

## Versioning

The project follows semantic versioning beginning with `1.0.0`.

Update `<VersionPrefix>` in the repository-level `Directory.Build.props` for
each release. It is the single source used to generate:

- `<AssemblyVersion>`
- `<FileVersion>`
- assembly/package metadata
- user-visible application version labels and logs
- exported metadata version information
- release artifact names

Add a corresponding historical entry to `CHANGELOG.md`. The release build
script reads the centralized version and does not maintain a separate copy.

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
