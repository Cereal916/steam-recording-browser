# Architecture

## Status

This document describes the production architecture for Steam Recording Browser 1.0.0.


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

### `PlayerWindow`

WPF player using `LibVLCSharp.WPF.VideoView`.

## Deployment boundary

The portable output includes:

- application EXE
- .NET self-contained runtime
- managed dependencies
- libVLC native binaries
- libVLC plugin directory

Nothing is installed globally.

## Intentionally not single-file

libVLC plugins/native modules are kept as real files in the deployment folder. This is a deliberate reliability decision, not an unresolved dependency.
