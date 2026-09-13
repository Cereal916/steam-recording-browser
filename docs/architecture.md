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

Saved clips use their enclosing `clip_<appid>_<yyyyMMdd>_<HHmmss>[_<suffix>]`
folder as a stable identity, including any numeric suffix. It takes precedence
over the nested `bg_*` or `fg_*` video session, which multiple independent clips
can share. Background recordings retain their `bg_<appid>_<yyyyMMdd>_<HHmmss>`
identity. Unrecognized paths use the original full path as a fallback.

Schema 3 migrates legacy saved-clip entries using their recorded path, without
requiring recording files to be online. A shared legacy entry belongs only to
the clip named by that path; overwritten per-clip history cannot be recovered.
An explicit clip entry takes precedence over a legacy entry for the same clip.
Loading a store that needs migration first preserves its original bytes in
`library_before_clip_identity_<timestamp>.json`, then saves the migrated store.
Imports apply the same identity migration and retain the pre-import safety
backup and original import file.

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
ffprobe to require both video and audio streams before reporting success. Users
can disable hardware candidates when they prefer slower, more efficient
software encoding.

### `AppLogger` and `LogViewerWindow`

`AppLogger` writes the application log under a lock and publishes completed
entries to in-process subscribers. The non-modal log viewer loads only a bounded
tail of the existing file, batches live UI updates, virtualizes its entry list,
and retains at most 5,000 displayed or paused entries. Closing the viewer stops
its timer and unsubscribes from logger events, leaving no ongoing UI overhead.

### Active rolling recordings

`RecordingScanner` distinguishes saved clips by Steam's ancestor `clip.pb`
metadata and treats unmatched `bg_*` sessions as automatic background
recordings. Automatic sessions are grouped into one library item per game and
served as consecutive DASH periods, with their boundaries shown on the player
timeline. `LiveRecordingService` marks an automatic recording live while its
manifest or media segments continue changing.

The normal static libVLC path remains unchanged for finalized recordings. An
active player lazily starts `LiveDashServer` on an ephemeral loopback port. The
server regenerates a dynamic compatibility MPD, serves existing fragments
directly with byte-range support, rejects fragments Steam is still writing, and
returns to a static MPD after recording activity stops. It does not copy or
retain video data and is disposed with the player window.

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
