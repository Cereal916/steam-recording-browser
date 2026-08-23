# Engineering tooling

This directory contains source-controlled engineering scripts used to build and
package Steam Recording Browser.

It is intentionally named `eng/` rather than `build/` so it cannot be confused
with generated build output.

Normal Rider/.NET development builds write compiler output beneath the project
`bin/` and `obj/` directories.

Production release artifacts are written only beneath:

```text
artifacts/publish/
```

To create a self-contained Windows x64 release from the repository root:

```powershell
./Build Release.cmd
```

`Build Release.cmd` invokes:

```text
eng/Build-Release.ps1
```

The release script:

- reads the semantic version from
  `src/SteamRecordingBrowser/SteamRecordingBrowser.csproj`
- restores dependencies
- publishes `win-x64` as self-contained
- validates the executable and bundled libVLC plugin tree
- downloads a checksum-verified FFmpeg 8.1 GPL build and bundles its separate
  executables, license, and corresponding-source notice
- writes release output to `artifacts/publish/`
- creates a portable ZIP

Release artifact names follow:

```text
SteamRecordingBrowser-<version>-win-x64/
SteamRecordingBrowser-<version>-win-x64.zip
```
