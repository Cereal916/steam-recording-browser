# ADR 0002: Keep Steam recordings immutable

- Status: Accepted
- Date: 2026-08-22

## Context

Steam owns the recording directory structure and original DASH manifests.

## Decision

Steam Recording Browser does not modify original `session.mpd` files or Steam
recording metadata.

When playback compatibility changes are required, the application generates a
separate `.SteamRecordingBrowser_playback.mpd` file beside the original
manifest.

Application metadata is stored separately under
`%LOCALAPPDATA%\SteamRecordingBrowser`.

## Consequences

The application can safely browse and play Steam recordings without mutating
Steam-owned source files.
