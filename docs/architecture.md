# Architecture

## Status

This document describes the current production architecture for Steam Recording Browser.


## Decision

Steam Recording Browser is now a native C# WPF application targeting .NET 10.

PowerShell is no longer used as an application runtime.

## Components

### `RecordingScanner`

Discovers `session.mpd`, calculates folder size, resolves recording timestamps, reads duration from MPD, finds Steam thumbnails, and applies persisted metadata.

### `SteamService`

Discovers Steam library folders and resolves App IDs to installed game names from `appmanifest_*.acf`.

### `MetadataService`

Owns `%LOCALAPPDATA%\SteamRecordingBrowser\library.json`.

Primary identity is the stable recording key derived from the Steam background recording folder:

`bg_<appid>_<yyyyMMdd>_<HHmmss>`

The original full path remains a legacy/fallback identity.

### `DashCompatibilityService`

Preserves the v22 playback fix:

- detects non-zero DASH Period start;
- resets Period start to zero;
- carries media presentation duration onto Period duration;
- removes static `timeShiftBufferDepth`;
- inspects the first fragmented MP4 segment for `tfdt`;
- injects `presentationTimeOffset`;
- saves a stable compatibility manifest beside the original recording.

### `LibVlcService`

Owns one `LibVLC` instance for application lifetime.

Used for:

- integrated playback;
- MP4 stream-copy/remux export.

### `FfmpegExportService`

Invokes the separate bundled FFmpeg tools for H.264, HEVC, and AV1 exports.
It selects supported NVIDIA NVENC, Intel Quick Sync, or AMD AMF encoders first
and falls back to libx264, libx265, SVT-AV1, or libaom software encoding.

The service parses machine-readable progress, supports cancellation, removes
failed partial files, retries after unsupported hardware encoders, and uses
ffprobe to require both video and audio streams before reporting success.

### `PlayerWindow`

WPF player using `LibVLCSharp.WPF.VideoView`.

## Deployment boundary

The portable output includes:

- application EXE
- .NET self-contained runtime
- managed dependencies
- libVLC native binaries
- libVLC plugin directory
- separate `ffmpeg.exe` and `ffprobe.exe` tools plus their license/source notice

Nothing is installed globally.

## Intentionally not single-file

libVLC plugins/native modules are kept as real files in the deployment folder. This is a deliberate reliability decision, not an unresolved dependency.
