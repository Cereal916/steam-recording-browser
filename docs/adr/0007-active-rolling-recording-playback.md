# ADR 0007: Proxy active rolling recordings through loopback DASH

- Status: Accepted
- Date: 2026-08-23

## Context

Steam background recordings are rolling MPEG-DASH sessions. Their manifest and
newest fragments change while a game is running, whereas the existing libVLC
playback path creates a static compatibility-manifest snapshot. That snapshot
cannot reliably follow the newest segments and may expose a fragment before
Steam finishes writing it.

## Decision

- Identify saved clips by Steam's `clip.pb` metadata and automatic recordings
  by unmatched `bg_*` session folders.
- Present automatic recording sessions as one per-game library item. Preserve
  session boundaries as consecutive DASH periods and timeline markers instead
  of creating a separate tile for each session.
- Consider an automatic recording live while its MPD or media fragments have
  been modified within the active-write window.
- Keep finalized recordings on the existing static local-file playback path.
- For an active player only, start an ephemeral loopback TCP server that serves
  a refreshed dynamic compatibility MPD and the original fragments.
- Support HTTP byte ranges, disable manifest caching, and temporarily reject a
  fragment while Steam is still writing it.
- Keep a short suggested presentation delay so libVLC follows the newest stable
  fragment instead of repeatedly consuming an incomplete tail.
- Return the manifest to static mode when Steam stops writing, and dispose the
  server when the player closes.
- Never expose the bridge beyond `127.0.0.1`, accept only known manifest and
  flat media-fragment paths, and never buffer or duplicate the rolling video.

## Consequences

Active background recordings can follow newly completed fragments with small,
bounded CPU and memory overhead. Playback remains limited by Steam's fragment
finalization cadence, so the newest playable frame may remain a few seconds
behind Steam's writer. The loopback HTTP implementation and dynamic MPD behavior
require dedicated tests because they are separate from finalized playback.
