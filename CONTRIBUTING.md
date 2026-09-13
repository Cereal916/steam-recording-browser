# Contributing

## Development workflow

1. Create a focused branch from `main`.
2. Make the smallest coherent change.
3. Add or update tests for deterministic logic where practical.
4. Use the isolated WPF UX tests and rendered images for UI changes (see
   `docs/development.md`). Keep testing off the user's active desktop. For
   player/libVLC/native behavior that needs an interactive session, use a
   separate VM or a session explicitly authorized by the user.
5. Run `dotnet build SteamRecordingBrowser.sln -c Release`.
6. Run `dotnet test --project tests/SteamRecordingBrowser.Tests/SteamRecordingBrowser.Tests.csproj`.
7. Open a pull request describing behavior changes, automated validation, and
   any remaining manual validation limits.

## Repository conventions

- Application code belongs under `src/`.
- Automated tests belong under `tests/`.
- Build/packaging scripts belong under `eng/`.
- Architecture decisions belong under `docs/adr/`.
- Do not commit user-specific recording paths, metadata, logs, or generated
  `.SteamRecordingBrowser_playback.mpd` files.
- Keep third-party dependency versions centralized in `Directory.Packages.props`.

## Player changes

Changes involving libVLC, DASH compatibility, timeline input, seeking, or
native Windows hooks should include manual verification notes because these
paths depend on native multimedia/window behavior that unit tests cannot fully
exercise.


## Versioning

Public releases follow semantic versioning beginning with `1.0.0`.

Do not introduce internal iteration-style release numbers into source, docs, or
build artifacts. Historical `30.x` numbers belong only in `CHANGELOG.md`.
