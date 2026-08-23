# ADR 0006: Use a bundled FFmpeg executable for export transcoding

- Status: Accepted
- Date: 2026-08-23

## Context

libVLC reliably remuxes recordings when the original codec is retained, but
its packaged encoder set does not reliably produce video for H.264, HEVC, or
AV1 exports. Exported files must also provide useful progress, cancellation,
hardware acceleration where available, and verification before success is
reported.

## Decision

- Keep libVLC for playback and Original codec remux exports.
- Invoke FFmpeg as a separate executable for H.264, HEVC, and AV1 exports.
- Prefer compatible hardware encoders and fall back to software encoders by
  default, while allowing users to require software encoding for better
  compression efficiency and consistent CPU-based output.
- Report percentage, speed, and ETA; cancellation terminates the process tree
  and removes the partial output.
- Verify every completed export with ffprobe and require both video and audio
  streams before reporting success.
- Bundle the BtbN Windows x64 FFmpeg 8.1 GPL build. Never use a build configured
  with `--enable-nonfree`.
- Keep FFmpeg replaceable under the application `ffmpeg` directory and include
  its license, binary checksum, exact corresponding-source revision, and build
  recipe link in every release package.
- Retain access to complete corresponding source for at least three years after
  distributing a bundled binary and honor source requests through the project
  issue tracker.

## Consequences

Codec exports are reliable and independently validated, but portable releases
are substantially larger. FFmpeg and its dependencies remain under their own
licenses and are not relicensed under the application's MIT license. Release
maintainers must preserve the acquisition, checksum, source, and notice files.
