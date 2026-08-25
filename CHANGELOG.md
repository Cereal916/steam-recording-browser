# Changelog

All notable changes to Steam Recording Browser are documented here. Each
version describes the net user-visible and maintainability changes from the
previous public release. Internal development iterations are intentionally
omitted.

The project follows semantic versioning beginning with **1.0.0**.

## Unreleased

## 1.5.0 - 2026-08-24

### Added

- Added a compact table layout with sortable, resizable, reorderable, and
  selectively visible columns; column-specific filters; persistent table
  customization; and delayed video previews when hovering rows.
- Added recording-information tooltips to clip layouts and the player with
  codec, resolution, frame-rate, bitrate, audio, file, session, and available
  Steam clip metadata details.

### Improved

- Refined list-view recording labels and information controls to keep artwork
  unobstructed and make recording metadata easier to scan.

### Fixed

- Corrected Steam session timestamps from UTC to the user's local timezone.

## 1.4.0 - 2026-08-24

### Added

- Added automatic classification and searchable labels for background
  recordings, saved clips, and actively recording sessions.
- Combined background-recording sessions into one recording per game, with
  session-boundary markers and timestamps on the player timeline.
- Added near-live playback for active rolling recordings with a live indicator,
  historical seeking, and a **Go live** control.
- Added automatic 10 MB log rotation with five retained archives, bounded log
  storage, archive-management controls, secret and user-path redaction, and
  reliable shutdown flushing.
- Added current-session filtering, Debug severity controls, selection, copying,
  and a dark context action to the live log viewer.

### Improved

- Debounced and batched library searching to keep typing responsive in large
  recording collections.
- Delayed enlarged clip previews until hover intent is clear, reducing decoder
  churn while scrolling.
- Reduced repetitive native diagnostics while retaining actionable live-player
  and DASH troubleshooting details.

### Fixed

- Fixed restarted games showing only the newest recording session instead of
  the complete retained recording history.
- Fixed active rolling recordings opening at zero duration, stalling near the
  live edge, requesting deleted segments, or failing to continue as Steam adds
  new segments.
- Fixed older finalized background sessions being incorrectly marked as live.
- Fixed live and HDR playback initialization exposing white frames, detached
  native VLC windows, or unstable hardware-decoding output.
- Fixed clip-card and timeline preview players interfering with the main player
  or creating independent native video windows.

## 1.3.0 - 2026-08-23

### Added

- Added a dark-themed live application-log viewer with virtualized bounded
  history, pause and resume, independent severity filters, search, clear,
  smart auto-scroll, and access to the raw log file.

### Fixed

- Fixed the native light corner appearing where horizontal and vertical
  scrollbars meet in dark-themed scrolling surfaces.

## 1.2.0 - 2026-08-23

### Added

- Added bundled FFmpeg transcoding for H.264, HEVC, and AV1 exports with
  hardware-encoder fallback, optional software encoding, progress and ETA,
  cancellation cleanup, and output validation.
- Added a dark codec-selection window with source details, bitrate and file-size
  estimates, and consolidated upload guidance.
- Added codec-labeled export filenames and temporary `.incomplete.mp4` files
  that are promoted only after validation succeeds.
- Added licensing notices, checksum verification, and corresponding-source
  metadata for the bundled FFmpeg build.

### Changed

- Retained lossless libVLC remuxing for Original exports while routing
  re-encoded exports through FFmpeg.
- Updated release packaging to acquire and verify FFmpeg and include its
  executable, source information, and license notices.

### Fixed

- Fixed transcoded exports producing audio-only MP4 files.
- Fixed Original exports being validated before libVLC finalized the file.
- Fixed export progress fill, cancellation cleanup, and clipped export-window
  actions.

## 1.1.2 - 2026-08-22

### Added

- Added mute and volume controls to the video player.
- Added polished dark styling for volume, timeline, and seek controls.

### Fixed

- Fixed player audio remaining silent until the volume control was moved.
- Fixed .NET 10 release and CI tests using the wrong test runner.

## 1.1.1 - 2026-08-22

### Added

- Added release-package license and third-party notices.
- Added SHA-256 checksums for portable release ZIP files.
- Added issue templates, dependency automation, CodeQL analysis, and expanded
  installation, troubleshooting, contribution, and security documentation.

### Fixed

- Fixed CI builds by keeping development builds framework-dependent and using
  self-contained output only during release publishing.
- Updated GitHub Actions to Node 24-compatible releases.

## 1.1.0 - 2026-08-22

### Added

- Added a dark-themed Settings window for application preferences, recording
  folder selection, metadata backup and import, and shortcut management.
- Added a first-run prompt and Settings action for creating or replacing a
  Windows desktop shortcut.

### Changed

- Consolidated preference and maintenance actions under Settings and refreshes
  the recording library after relevant setting or metadata changes.
- Applied dark styling to application scrollbars and native Windows title bars.

## 1.0.2 - 2026-08-22

### Refactored

- Renamed source-controlled release tooling from `build/` to `eng/` and kept
  generated production output under `artifacts/publish/`.

## 1.0.1 - 2026-08-22

### Fixed

- Fixed video click and mouse-wheel seeking on mixed-DPI and multi-monitor
  configurations by recognizing the complete native player window hierarchy
  and using Per-Monitor-V2 coordinate conversion.

## 1.0.0 - 2026-08-22

First production-ready public release.

### Added

- Added recursive Steam Game Recording discovery and local game-name resolution.
- Added list browsing with Steam thumbnails, favorites, descriptions, tags,
  search, filtering, and sorting.
- Added metadata backup and import with stable recording identities.
- Added integrated libVLC playback, timeline seeking, frame previews, and MP4
  remux export without modifying Steam's recordings.
- Added startup progress, self-contained Windows x64 packaging, Rider
  configuration, continuous integration, automated tests, and project
  documentation.
