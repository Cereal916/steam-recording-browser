# Changelog

All notable changes to Steam Recording Browser are documented here.

The project adopts semantic versioning beginning with **1.0.0**.

## 1.1.0 - 2026-08-22

### Added

- Added a dedicated dark-themed Settings window for application preferences.
- Added a first-run prompt offering to create a Windows desktop shortcut.
- Added desktop shortcut creation and replacement controls to Settings, with
  persisted prompt state, status detection, logging, and error handling.

### Changed

- Moved Steam Game Recording folder selection from the main window into
  Settings. The recording library reloads automatically when the folder changes.
- Moved metadata backup and import actions from the main toolbar into Settings.
- Metadata imports now refresh the active recording list and tag filters after
  completion.
- Applied dark styling to application scrollbars and native Windows title bars,
  including hover, drag, caption, border, and title-text states.
- Simplified the main toolbar by consolidating preference and maintenance
  actions under Settings.

## 1.0.2 - 2026-08-22

### Changed

- Renamed the source-controlled `build/` tooling directory to `eng/` so it is
  clearly distinguished from generated build output.
- Updated the release wrapper and documentation to invoke
  `eng/Build-Release.ps1`.
- Clarified that production release output is generated only under
  `artifacts/publish/`.

## 1.0.1 - 2026-08-22

### Fixed

- Fixed native video click and mouse-wheel seeking intermittently failing on
  particular monitors.
- Native input foreground detection now recognizes the WPF player window plus
  its libVLC child/owned HWND hierarchy.
- Low-level mouse-hook coordinates are now converted from Win32 screen pixels
  into the player window's current WPF DPI coordinate space before video and
  timeline hit-testing.
- Added explicit Per-Monitor-V2 DPI awareness for consistent behavior across
  mixed-DPI displays.

## 1.0.0 - 2026-08-22

First production-ready public release.

### Added

- Windows WPF desktop application for browsing Steam Game Recording clips
- Recursive Steam recording discovery
- Local Steam app-name resolution
- Existing Steam thumbnail support
- Favorites, descriptions, and tags
- Search, filtering, and sorting
- Metadata backup and import
- Stable recording identities
- DASH compatibility manifests without modifying original recordings
- Integrated libVLC playback
- Timeline seeking and frame preview
- MP4 export/remux
- Startup progress UI
- Self-contained Windows x64 release packaging
- JetBrains Rider shared run/debug configuration
- GitHub Actions CI
- Unit-test project
- Production-style repository layout and development documentation

### Repository

- Application source under `src/`
- Automated tests under `tests/`
- Release tooling under `build/`
- Architecture and ADRs under `docs/`
- Centralized NuGet package versions
- Repository-wide build and editor configuration
- Semantic versioning established at `1.0.0`

## Pre-1.0 development history

The entries below use the project's earlier internal `30.x` development build
numbers. They are retained for engineering history only and are not part of the
public semantic-version sequence.

## v30.1 compiler fix

The v30 project enabled both WPF and Windows Forms because the application uses
`FolderBrowserDialog` for selecting the recording root. With SDK implicit usings
enabled, that makes both `System.Windows.Application` and
`System.Windows.Forms.Application` visible in `App.xaml.cs`.

v30.1 explicitly qualifies the WPF startup types:

- `System.Windows.Application`
- `System.Windows.MessageBox`
- `System.Windows.MessageBoxButton`
- `System.Windows.MessageBoxImage`

This avoids changing implicit-usings behavior across the rest of the C# project.


## v30.2 desktop-framework cleanup

v30.1 still enabled Windows Forms solely for `FolderBrowserDialog`. That caused
WinForms types such as `Application`, `MessageBox`, `SaveFileDialog`, and
`OpenFileDialog` to collide with their WPF/Microsoft.Win32 counterparts.

v30.2 removes Windows Forms from the project completely.

The recording-folder picker now uses the native WPF/.NET
`Microsoft.Win32.OpenFolderDialog`, so the application remains a pure WPF
desktop project.

v30.2 also adds explicit `System.IO` imports to services that use `File`,
`Directory`, `DirectoryInfo`, `FileInfo`, `StreamWriter`, `SearchOption`, and
`InvalidDataException`. The source therefore no longer relies on desktop-SDK
implicit-using behavior for filesystem APIs.


## v30.3 dark-theme control polish

- ComboBoxes now use the same dark surface/border/text palette as the rest of the application.
- The opened dropdown popup is dark instead of the Windows default light theme.
- Hovered and selected dropdown items use darker blue-gray highlight states.
- The ComboBox arrow/button area is dark themed.
- `Favorites only` now explicitly uses the application's light foreground color.
- A default dark-text CheckBox style was added so future checkboxes inherit the correct foreground.


## v30.4 VLC-style timeline seeking

The original WPF timeline used the default `Slider` track-click behavior.
WPF implements a track click as `LargeChange`; because the media timeline is
normalized to `0..1`, the default step could move the value directly to `1`
and seek to the end of the recording.

v30.4 changes the player timeline to VLC-style interaction:

- clicking anywhere on the timeline seeks directly to that point;
- dragging the thumb still scrubs normally and seeks on release;
- seeking uses libVLC's absolute media time when the duration is known, which
  is more deterministic for Steam fragmented-DASH recordings;
- mouse wheel over the timeline seeks ±5 seconds;
- Left/Right arrow keys seek ±5 seconds;
- Page Up/Page Down seek ±30 seconds;
- Home/End seek to the beginning/end.

`IsMoveToPointEnabled` is enabled on the WPF Slider, with an explicit
coordinate-to-time implementation as a safeguard against control-template
behavior.


## v30.5 timeline click fix and thumbnail hover preview

### Timeline

Track-click seeking is now separated from thumb dragging.

For a normal timeline click the player:

1. calculates the exact normalized position from the mouse X coordinate;
2. suppresses WPF Slider/Track default click handling;
3. remembers that target through the mouse gesture;
4. performs the libVLC seek on mouse-up through the WPF dispatcher.

This prevents the Slider from restoring the old value after the click has
already attempted to seek.

Thumb dragging, mouse-wheel seeking, and keyboard seeking remain supported.

### Thumbnail hover

Hovering anywhere over a recording card now shows a large preview tooltip using
Steam's existing `thumbnail.jpg`.

The preview is 640×360 maximum content area, includes the game name and
recording timestamp, appears after a short delay, and does not generate or
extract any new thumbnails.


## v30.6 player actions, search clarity, and hover placement

### Hover preview

The enlarged Steam thumbnail preview is now anchored to the **right side of the
recording card** with a small horizontal gap. It no longer follows the mouse,
so the preview does not cover the cursor or the clip rows beneath it.

### Player actions

The integrated video player now includes:

- Favorite / unfavorite toggle
- Edit description
- Edit tags
- Export MP4

These controls update the same `RecordingItem` and `MetadataService` used by
the main browser, so changes are reflected in the main window without a
separate metadata copy.

The export action uses the same bundled-libVLC remux path as the browser's
context-menu export.

### Search bar

The filter area now visibly labels the field **Search clips** and shows the
placeholder:

`Game, description, or tag…`

The placeholder disappears as soon as search text is entered and returns when
the field is cleared.


## v30.7 player theme, smooth playhead, and clip-card sizing

### Player button theme

All buttons in `PlayerWindow` now use a shared dark WPF button style matching
the rest of Steam Recording Browser. The style includes:

- dark normal background;
- lighter hover state;
- darker pressed state;
- light text;
- blue focus border;
- dark disabled state.

### Smooth playback cursor

The player UI timer now refreshes at approximately 30 FPS instead of four times
per second.

libVLC's reported media time can arrive in coarse increments for some Steam DASH
recordings, so the UI now interpolates the displayed playhead between libVLC
time updates using `Stopwatch`. Whenever libVLC reports meaningful new progress,
the interpolated clock is re-anchored to the decoder's real media time.

Seeking resets the interpolation clock immediately.

This changes only the visual timeline/time display; libVLC remains the source
of truth for actual playback.

### Standard clip-card width

Recording cards in the main browser now use a consistent 1040-pixel width and
are centered inside the ListView. The ListView item content stretches across
the available row so every card uses the same alignment and width regardless
of its text content.


## v30.8 end-of-media replay and segment-stable playhead

### Replay after the video ends

libVLC's `EndReached` state is terminal for that playback session. v30.8 now
tracks this state explicitly.

- The Play button changes to **Replay** at the end.
- Clicking Replay starts a fresh libVLC playback session from 0:00.
- Clicking or dragging the timeline after the video has ended starts a fresh
  playback session and seeks to the selected location.
- Relative wheel/keyboard seeking after the end also restarts at the selected
  time.

The requested post-end seek is deferred until libVLC raises `Playing`, because
absolute media-time seeking is reliable only after the fresh playback session
is active.

### No segment-boundary bounce

Steam's fragmented DASH recordings can make libVLC briefly report a slightly
older media timestamp when crossing media segments.

The visual playhead is now **monotonic during uninterrupted playback**:

- decoder timestamps that move forward re-anchor the smooth clock;
- small backward timestamp corrections are ignored visually;
- explicit user seeks reset the monotonic clock immediately, so seeking
  backward still works normally;
- pause/stopped states follow libVLC's exact reported time.

This removes the visible backward bounce without changing actual decoding or
audio/video playback timing.


## v30.9 clock-driven playhead smoothing

v30.8 still re-anchored the visible cursor too closely to libVLC's reported
fragmented-DASH timestamp. On some Steam recordings, those timestamps wobble at
segment transitions and made the UI look more jittery.

v30.9 uses a different model:

- the visible playhead advances from a local high-resolution `Stopwatch`;
- the UI refreshes at approximately 60 FPS;
- libVLC remains the source of truth for actual playback;
- decoder drift is sampled only every 250 ms;
- drift under 250 ms is ignored as normal DASH timestamp wobble;
- moderate drift is corrected gradually, capped at 35 ms per correction;
- only very large drift (1.2 seconds or more) snaps the display back to the
  decoder's reported time;
- explicit seeks, replay, pause, and resume reset the display clock
  immediately.

This prevents the UI from chasing every segment timestamp while still keeping
the cursor synchronized with actual playback over time.


## v30.10 seek-settling protection

After an explicit timeline seek, libVLC can briefly continue reporting the old
pre-seek timestamp while the fragmented-DASH decoder establishes the new
position. v30.9's large-drift protection could mistake that stale timestamp for
a real discontinuity and visibly move the cursor backward before libVLC caught
up.

v30.10 adds an explicit **seek-settling state**:

- the requested seek target immediately becomes the display-clock anchor;
- stale decoder timestamps are ignored for up to 1.5 seconds;
- normal synchronization resumes as soon as libVLC reports a timestamp within
  500 ms of the requested target;
- if the grace period expires, the UI still does not snap backward;
- regular playback drift correction remains unchanged outside this seek window.

This is specifically designed to remove the
`seek → jump backward → jump forward` visual sequence while retaining the
smooth v30.9 playhead during uninterrupted playback.


## v30.11 visual scrubbing and timeline frame preview

### Drag scrubbing

Dragging the timeline thumb now updates the **actual main video frame** while
you drag.

- If playback was running, it pauses for the drag.
- The main libVLC player seeks to the dragged location approximately every
  80 ms so the image follows the cursor without overwhelming Steam's
  fragmented-DASH decoder.
- Releasing the thumb performs the final seek.
- Playback automatically resumes if it had been playing before the drag.

### Hover frame preview

Hovering over the timeline now opens a frame preview above the seek bar.

The preview uses a **second muted libVLC MediaPlayer** backed by the same
compatibility manifest. It does not alter the main player's playback position.

- preview size: 320×180;
- timestamp shown below the preview frame;
- popup follows the horizontal mouse position;
- popup stays above the timeline;
- preview decoder seeks are throttled to roughly 8 fps;
- moving the mouse does not pause or seek the main video.

The preview player is reused for the lifetime of the player window and is
disposed with the main player.


## v30.12 timeline-preview positioning

The hover frame preview is now centered directly over the mouse pointer.

- the popup's horizontal center follows the cursor;
- the bottom edge sits 12 pixels above the cursor/timeline;
- near the left or right edge, the popup is clamped just enough to remain
  inside the timeline/player area;
- frame decoding and hover-seek behavior are otherwise unchanged.


## v30.13 cursor-anchored timeline preview

The timeline preview now uses WPF `RelativePoint` placement instead of
`Relative` placement.

The popup's anchor rectangle is moved to the exact mouse X coordinate on every
mouse move, and the popup is offset left by half of its own width. This makes
the center of the frame preview line up directly over the pointer.

Preview frame updates are also more frequent:

- previous throttle: 120 ms (~8 fps);
- new throttle: 40 ms (~25 fps).

The secondary muted libVLC player is still reused, so hover previews remain
independent from the main playback position.


## v30.14 absolute cursor preview and explicit drag scrubbing

### Preview position

The timeline preview no longer relies on WPF relative-popup coordinate
behavior.

On every mouse move:

1. the mouse point on the timeline is converted with `PointToScreen`;
2. device pixels are converted back to WPF device-independent coordinates;
3. the popup uses `AbsolutePoint` placement at that exact screen location;
4. half the preview width is subtracted so its center is directly over the
   pointer;
5. the popup is placed 12 pixels above the pointer.

This avoids the coordinate-space mismatch that kept the preview shifted left
on some DPI/display configurations.

### Smoother hover previews

Hover preview seek requests are now allowed every 25 ms instead of every
40 ms. The secondary libVLC player is still reused.

### Drag scrubbing

Dragging no longer depends on WPF Slider `ValueChanged` events.

Once the thumb is grabbed, every mouse move directly calculates the normalized
timeline position from the pointer X coordinate, updates the thumb, and sends a
throttled seek to the main libVLC player.

Main-video scrub updates are now allowed every 50 ms (~20 fps), with a final
exact seek on mouse release.


## v30.15 compile fix

v30.14 introduced explicit mouse-driven drag scrubbing, which calls
`GetNormalizedMousePosition` from both `PreviewMouseDown` and
`PreviewMouseMove`.

The helper still accepted only `MouseButtonEventArgs`, while mouse-move events
supply `MouseEventArgs`. The helper now accepts the common `MouseEventArgs`
base type, which supports both call sites.

The stale build-script banner was also corrected from v30.2.0 to v30.15.0.


## v30.16 direct cursor preview and press-drag-release timeline seeking

### Timeline hover preview position

The hover preview no longer uses `PlacementRectangle`, `PlacementTarget`,
timeline-width clamping, or relative placement calculations.

It now uses WPF `Placement=Absolute` and writes the preview's screen position
directly:

- horizontal = exact mouse screen X minus half the preview width;
- vertical = exact mouse screen Y minus preview height minus an 8 px gap.

The pointer location comes from `Mouse.GetPosition(Timeline)` followed by
`PointToScreen`, with DPI conversion back to WPF device-independent units.

This makes the preview's center directly track the physical mouse cursor.

Hover preview seek requests are now allowed every 16 ms (~60 requests/sec).
Actual visible frame cadence is still limited by libVLC/DASH decoding.

### Press, drag, preview, release

Any left mouse press anywhere on the timeline now begins a scrub operation.

- mouse-down pauses playback if it was playing;
- the frame at the initial press position is shown immediately;
- holding the button and moving left/right previews frames continuously;
- scrub frame seeks are allowed every 30 ms (~33/sec);
- no timeline location is committed on mouse-down;
- mouse-up commits the final release position;
- playback resumes automatically only if it had been playing before scrubbing.

A simple click is therefore just a zero-distance press/release scrub.


## v30.17 warning cleanup and publish-version fix

- Removed obsolete `_trackClickPending` and `_pendingTrackPosition` fields left
  behind by the pre-v30.16 click-to-seek implementation.
- Removed obsolete assignments/references tied to those fields.
- Corrected stale version strings in `Build Release.ps1`, including the publish
  directory and portable ZIP name.
- The build should now publish under `publish\Steam Recording Browser v30.17.0\`
  instead of the stale v30.14.0 folder.


## v30.18 reliable scrubbing when playback is active

Scrubbing already worked correctly when the video was paused, but starting a
scrub while playback was active could continue showing normal playback frames.

The reason is that libVLC `Pause()` is asynchronous. v30.17 immediately began
issuing `MediaPlayer.Time` changes after calling `Pause()`, before the decoder
had actually entered its paused state.

v30.18 now:

- subscribes to libVLC's `Paused` event;
- records the latest requested scrub location while pause is pending;
- does not issue scrub-frame seeks during that transition;
- as soon as `Paused` is confirmed, seeks to the newest mouse position;
- subsequent drag movement uses the existing live frame-scrub behavior;
- if the mouse is released quickly, the final release position is still
  committed and playback is resumed when appropriate.

This makes scrubbing from a playing state follow the same decoder path as
scrubbing a video that was already paused.


## v30.19 independent scrub-frame renderer and player metadata display

### Reliable scrub frames during active playback

The main libVLC player's pause transition is no longer used to render scrub
frames.

A third muted libVLC MediaPlayer is now dedicated to drag scrubbing and is
displayed directly over the normal video area while the mouse button is held
on the timeline.

- pressing the timeline shows the scrub-preview layer immediately;
- the preview decoder seeks independently of the main player's play/pause
  state;
- dragging requests preview frames up to every 25 ms (~40/sec);
- a timestamp badge is shown at the bottom of the preview;
- releasing the mouse hides the preview layer;
- the main player then seeks once to the final release position;
- playback resumes if it had been playing before the scrub began.

This avoids the asynchronous `Pause()` behavior that prevented frame updates
while scrubbing a playing recording.

### Description and tags in PlayerWindow

The bottom of PlayerWindow now has a persistent metadata row showing:

- Description
- Tags

Empty values display `No description` / `No tags`. The displayed values update
immediately after using the existing Description… or Tags… edit buttons.


## v30.20 active preview decoders and diagnostics

Both timeline-preview decoders now remain actively playing (muted) while they
are being used. The app continuously moves their `MediaPlayer.Time` rather
than repeatedly seeking a paused libVLC player, which can stop presenting new
frames with Steam fragmented-DASH media.

- hover preview resumes its decoder while the pointer is over the timeline;
- scrub preview keeps its dedicated decoder running for the entire drag;
- each decoder pauses only when its preview interaction ends;
- preview start/seek/restart operations are protected with exception logging;
- libVLC `EncounteredError` events are logged;
- stalled/delayed preview seeks are logged when actual decoder time remains at
  least 1.5 seconds from the requested frame;
- failed preview decoders are restarted and the requested seek is retried.

Diagnostics are written to the existing `SteamRecordingBrowser.log` opened by
the application's **Open log** button.


## v30.21 single-player scrubbing and paused-only animated hover preview

### Scrubbing

Drag scrubbing no longer uses a secondary libVLC decoder.

When a drag begins while the recording is playing, the main player is
explicitly paused with `SetPause(true)`. The application waits for libVLC's
`Paused` event, then seeks the main player itself as the pointer moves.

This intentionally uses the same code path that already rendered scrub frames
correctly when playback was paused.

A quick click/release that finishes before pause confirmation is retained as a
pending final seek and is committed from the `Paused` event before playback is
resumed.

### Hover preview

Animated hover playback is now available only while the main recording is
paused. This avoids running a second DASH decoder while the main recording is
actively decoding.

While the main recording is playing, the popup still follows the mouse and
shows the requested timestamp, but its video area displays `Pause to preview`
instead of starting the secondary decoder.

### Logging

Scrub start, pause confirmation, deferred releases, hover-preview suspension,
and exceptions are written to the existing application log.


## v30.22 static hover frames during playback, robust main-player scrubbing, metadata layout fix

### Hover previews

- While the main video is paused, the existing animated "play from here"
  preview remains.
- While the main video is playing, the hover decoder is woken briefly, seeks
  to the requested location, renders that frame, and is paused again after
  about 90 ms.
- This provides an individual frame preview without leaving a second DASH
  decoder continuously playing alongside the main video.
- Hover preview failures and pause/restart state continue to be logged.

### Drag scrubbing

Drag scrubbing always uses the main player.

When a scrub starts during playback, the application requests an explicit
pause and now uses both:
1. libVLC's `Paused` event; and
2. a 15 ms state poll.

Whichever confirms the paused state first activates the known-working paused
scrub path. The frame on the main player then follows the timeline location as
the pointer moves. Quick release before pause confirmation is still deferred
and committed safely.

### Description and tags

The Description/Tags panel was accidentally nested inside the video Grid even
though it specified `Grid.Row=2`. It has been moved into row 2 of the actual
player-controls Grid, below the timeline and action buttons.


## v30.23 direct video-surface controls

The main video surface now supports VLC-style direct interaction:

- left-click while playing pauses the video;
- left-click while paused resumes playback;
- left-click after `EndReached` replays from the beginning;
- mouse-wheel up seeks forward 5 seconds;
- mouse-wheel down seeks backward 5 seconds.

The wheel behavior uses the same `SeekRelative` path as the existing timeline
wheel and keyboard controls.

Video-surface play/pause and wheel-seek actions are written to the existing log,
including current media time and libVLC state when the interaction occurs.


## v30.24 higher-frequency timeline scrubbing

Main-player scrub seek requests now update approximately every 16 ms
(~60 requests/sec) instead of every 30 ms (~33 requests/sec).

The final exact seek on mouse release is unchanged. Actual unique frame cadence
still depends on libVLC, keyframe spacing, and Steam's fragmented-DASH media,
but the application no longer imposes the previous ~33 fps scrub ceiling.


## v30.25 GitHub readiness and input fixes

### Portable recording-folder configuration

The application no longer contains a developer-specific recording path.

At startup it now:

1. loads the last valid recording folder from
   `%LOCALAPPDATA%\SteamRecordingBrowser\settings.json`;
2. if no saved folder is available, checks the installed Steam location for
   `userdata\<account>\gamerecordings` directories and prefers one containing
   recordings;
3. if nothing can be detected, asks the user to choose the Steam Game
   Recording folder.

A valid manually-entered/browsed recording folder is persisted for the next
launch. `settings.json` is created at runtime and is not part of the source
repository.

Steam itself allows the recording folder to be changed, so automatic discovery
is intentionally only a first-run convenience; Browse remains the authoritative
fallback.

### Build version source of truth

`Build Release.ps1` now reads `<Version>` directly from
`SteamRecordingBrowser.csproj`. The publish folder, portable ZIP name, and
build banner are generated from that value instead of duplicating a hardcoded
version in the script.

### Git ignore

The repository includes a `.gitignore` covering Visual Studio/.NET build
artifacts, the local `publish` folder, ZIP releases, runtime log files, and
common editor/OS files.

### Player input

- Mouse-wheel seeking is handled at the PlayerWindow level, so wheel up/down
  seeks ±5 seconds regardless of which player-window control the pointer is
  over.
- The video click target is now a transparent WPF interaction layer inside the
  LibVLCSharp `VideoView`. This avoids the native video child window consuming
  clicks before the surrounding WPF Grid can see them.
- Clicking the video toggles pause/play; clicking after EndReached replays.
- The Play/Pause button also uses explicit `SetPause(true/false)` state changes.

### Privacy / machine-specific scan

Before packaging v30.25, the source tree was scanned for the previous
developer-specific recording path and common local-user path patterns. No
personal Windows user path is required by the application.


## v30.26 native player-window input routing

The previous video-click, Space, and global mouse-wheel handlers still depended
on WPF routed input. LibVLCSharp's WPF `VideoView` contains a native child HWND,
so those messages can bypass WPF whenever the video surface owns focus.

v30.26 registers a `ComponentDispatcher.ThreadFilterMessage` hook while a
PlayerWindow is open. This sees Windows messages before they are dispatched to
either WPF controls or libVLC's native video child.

- `WM_LBUTTONDOWN` toggles play/pause when the physical cursor is inside the
  main VideoView rectangle.
- `WM_KEYDOWN` for Space toggles play/pause immediately, including before any
  WPF button has been clicked. Auto-repeat keydown messages are ignored.
- `WM_MOUSEWHEEL` seeks ±5 seconds whenever the physical cursor is anywhere
  inside the PlayerWindow rectangle, including over the native video surface,
  timeline, buttons, metadata area, or other WPF controls.
- Space is not intercepted while a WPF text/password field has keyboard focus.
- The old WPF video-click and window-wheel handlers were removed to prevent
  duplicate input handling.
- The native hook is attached only while PlayerWindow is open and is detached
  on close.
- Native input actions and failures are written to the existing application log.


## v30.27 animated clip-card hover previews

The clips browser now uses one reusable muted libVLC player for hover previews.

Behavior:

- entering a clip card opens the existing large preview immediately with
  Steam's `thumbnail.jpg`;
- after a 300 ms hover delay, the thumbnail transitions to a muted moving
  preview of that recording;
- the moving preview uses the same compatibility MPD path as PlayerWindow;
- moving away before the delay expires never starts libVLC;
- leaving the card stops playback, disposes that preview Media object, closes
  the popup, and restores thumbnail mode;
- moving to another card reuses the same MediaPlayer instead of creating a
  decoder per clip;
- a long hover loops the preview after EndReached;
- if libVLC cannot start or encounters an error, the popup falls back to the
  Steam thumbnail when one exists;
- preview start, stop failures, media errors, and fallback failures are written
  to `SteamRecordingBrowser.log`.

Only one additional video decoder can be active from the clips browser at a
time, regardless of how many recordings are displayed.


## v30.28 native-video input fix and zoom/pan

### Native libVLC input

The PlayerWindow no longer relies on WPF `ComponentDispatcher` messages for
video-surface input. LibVLC's WPF VideoView hosts a native child HWND, and input
over that child can bypass the WPF dispatcher entirely.

While PlayerWindow is the foreground window, v30.28 installs Windows
`WH_MOUSE_LL` and `WH_KEYBOARD_LL` hooks and filters them to the physical
PlayerWindow/video rectangles.

This means:

- clicking directly on the rendered video toggles play/pause;
- Space toggles play/pause even when the native libVLC video surface has focus;
- normal mouse-wheel movement anywhere over PlayerWindow seeks ±5 seconds,
  including directly over the video;
- the hooks are removed as soon as PlayerWindow closes;
- hook setup/teardown and failures are written to the application log.

### Zoom

Video zoom uses libVLC `CropGeometry`, so it operates on the actual decoded
video instead of trying to apply a WPF transform to libVLC's native HWND.

Controls:

- `Ctrl + mouse wheel` anywhere over PlayerWindow changes zoom in 25% steps;
- zoom range is 100% through 400%;
- the current zoom percentage is displayed beside the playback time;
- `Reset zoom` returns to 100%;
- double-clicking the video resets zoom to 100%;
- while zoomed above 100%, left-click-drag directly on the video pans the
  visible crop;
- a stationary click still toggles play/pause;
- normal wheel without Ctrl continues to seek ±5 seconds.

Zoom is session-only. Every newly opened PlayerWindow starts at 100%.

Crop geometry, zoom changes, panning failures, and input-hook failures are
logged to `SteamRecordingBrowser.log`.


## v30.29 deterministic click-drag video panning

Zoom already worked through libVLC crop geometry, but the first panning
implementation applied small incremental mouse deltas through queued Dispatcher
callbacks. Because the native video click is intentionally suppressed, those
incremental drag updates were not reliable.

v30.29 now:

- records the mouse-down screen coordinate and current pan position;
- detects a drag after 3 physical pixels of movement while zoomed above 100%;
- computes each pan position from the *total displacement since mouse-down*
  instead of accumulating per-event deltas;
- uses normalized VideoView dimensions and the current zoom level to map the
  drag to the full available crop range;
- preserves the "grab the image" interaction: drag right/down to move the image
  right/down;
- retains stationary click = play/pause;
- logs video mouse-down, drag start, final drag position, zoom, and pan values.

This avoids lost/queued mouse-delta updates while keeping the low-level native
input handling required by libVLC's child video window.


## v30.30 libVLC 3.x crop-window panning fix

The drag gesture was being detected correctly, but the video content was not
moving as expected. The issue was the crop-window geometry encoding.

With libVLC 3.x, offset `CropGeometry` values behave differently from the
intuitive `width x height + x + y` form. For reliable pan/zoom, the first two
values must be encoded as the crop rectangle's right and bottom edges:

`(x + width) x (y + height) + x + y`

v30.30 now computes a logical crop rectangle and converts it to that libVLC
3.x-compatible geometry before assigning `MediaPlayer.CropGeometry`.

The log now records both:

- the logical crop rectangle (`left`, `top`, `width`, `height`);
- the exact geometry string sent to libVLC;
- source dimensions, zoom, and normalized pan position.

This preserves all v30.29 input/drag behavior while correcting the actual video
window movement.


## v30.31 startup splash/loading window

The application now shows a splash window immediately when the process starts.

- The splash uses the existing Steam Recording Browser logo and dark theme.
- It displays an indeterminate loading bar and startup status text.
- `App.xaml` no longer launches MainWindow through `StartupUri`; startup is
  controlled explicitly from `App.OnStartup`.
- WPF is allowed to render the splash before MainWindow is constructed.
- The splash remains visible while the main browser initializes and is closed
  automatically after MainWindow has rendered.
- Startup exceptions are logged and shown to the user instead of silently
  leaving a stuck splash screen.
- The main browser continues its normal asynchronous recording scan after it
  appears, so the app feels responsive immediately instead of looking like
  nothing is happening.


## v30.32 zoom/pan removal

The experimental video zoom and pan feature has been removed for now.

Removed:

- Ctrl + mouse wheel zoom;
- zoom percentage indicator;
- Reset zoom button;
- double-click zoom reset;
- click-drag panning;
- libVLC crop-geometry manipulation;
- zoom/pan state and related logging.

Retained:

- direct click on the native libVLC video surface toggles play/pause;
- Space toggles play/pause;
- mouse wheel anywhere in PlayerWindow seeks ±5 seconds;
- low-level native input hooks remain in place for reliable interaction over
  libVLC's child video window.


## v30.33 compile fix after zoom/pan removal

v30.32 removed the experimental zoom/pan feature, but that cleanup
accidentally removed two shared native-input helpers that are still required:

- `TogglePlayback(string source)`
- `IsTextEntryFocused()`

Those helpers have been restored. Zoom and click-drag panning remain removed.

`App.xaml.cs` also now imports `SteamRecordingBrowser.Services` so the startup
splash error path can resolve `AppLogger`.

No playback behavior was intentionally changed from v30.32:
- direct video click still toggles play/pause;
- Space still toggles play/pause;
- mouse wheel anywhere in PlayerWindow still seeks ±5 seconds;
- the startup splash remains enabled.


## v30.34 determinate startup progress

The startup splash now reports actual initialization progress instead of an
empty/indeterminate progress bar.

The splash is displayed before libVLC initialization and shows:

- a 0–100% determinate progress bar;
- the current startup stage;
- a numeric percentage;
- actual clip-scan progress (`Loading clip X of Y: Game Name`).

Startup phases include application initialization, video-engine startup,
metadata loading, recording-folder discovery, Steam game-name discovery,
recording discovery, per-clip loading, filter construction, and final browser
preparation.

The recording scan receives the largest part of the progress range (48–92%)
and advances from the scanner's actual processed/total clip counts rather than
from a timer.

The splash now remains visible until the initial library scan is complete,
briefly displays 100% / Ready, and then closes. Subsequent manual Refresh
operations continue to use the progress UI inside MainWindow rather than
reopening the splash.


## v30.36 coalesced hardware-assisted timeline scrubbing

v30.36 is rebuilt from the intact v30.34 source after v30.35 accidentally
removed unrelated timeline-hover methods.

Scrubbing now uses a latest-position-wins pump:

- mouse movement records only the newest requested position;
- an 8 ms dispatcher pump checks for work;
- when libVLC is keeping up, frame seeks are issued at up to ~40/sec;
- when the decoder falls behind, the app backs off to ~14/sec instead of
  creating an asynchronous seek backlog;
- stale intermediate positions are discarded;
- after setting `MediaPlayer.Time` on the paused player, `NextFrame()` requests
  immediate decode/presentation of the new frame;
- the timeline/time display stays anchored to the requested scrub position;
- mouse release still performs one exact final seek.

Playback media requests `:avcodec-hw=any`, allowing libVLC/FFmpeg to use
available hardware decoding while retaining software fallback.

Scrub lag of 750 ms or more is logged with requested time, actual decoder time,
player state, FPS, and hardware-decoding preference.

All timeline-hover preview handlers and helpers from v30.34 are preserved.


## v30.37 active-decoder timeline scrub overlay

The v30.36 diagnostics showed that the paused main DASH player was not moving
during drag seeks at all (`actual=0ms`), even while requested positions changed
by many seconds. The secondary timeline-hover decoder, however, was successfully
decoding arbitrary positions while in the Playing state.

v30.37 therefore changes the scrub architecture:

- dragging the timeline no longer asks the paused main player to render every
  intermediate frame;
- the existing muted secondary preview decoder is reused as a temporary
  full-size `VideoView` overlay directly over the main video;
- that decoder remains actively decoding while drag seeks are issued;
- mouse movement is still latest-position-wins, so stale intermediate seeks are
  discarded;
- target cadence is ~30 updates/sec when the decoder is keeping up and ~15/sec
  when it falls behind;
- hardware decoding remains requested through `:avcodec-hw=any`;
- when the drag ends, the overlay is hidden and the main player receives only
  the final exact seek;
- the reusable preview player is paused afterward so normal timeline-hover
  preview can use it again.

This avoids the libVLC paused-DASH seek path that the v30.36 logs proved was
stuck at 0 ms.

New diagnostics report `Timeline scrub preview lag` using the active preview
decoder's actual time and FPS.


## v30.38 scrub-overlay compile fix

v30.37 incorrectly attempted to replace `_previewMedia`, which is a readonly
field owned for the lifetime of `PlayerWindow`. That caused CS0191, plus a
nullable warning on the following dereference.

The scrub overlay now restarts the existing hover-preview decoder with
`RestartHoverPreview(...)` at the current scrub target instead of creating and
assigning a replacement Media instance.

The v30.37 active secondary-decoder scrub architecture is otherwise unchanged.


## v30.39 dedicated active scrub decoder

The v30.38 scrub overlay reused the same `MediaPlayer` used by the small
timeline-hover preview and attached it to a second `VideoView`. libVLC's native
video output should not be shared across two simultaneous WPF VideoView
surfaces; the result was a frozen/white scrub overlay even though the decoder
reported `Playing`.

v30.39 gives timeline drag scrubbing its own dedicated decoder:

- `_scrubPlayer` is a separate muted `MediaPlayer`;
- `ScrubPreviewVideoView` is attached only to `_scrubPlayer`;
- normal `PreviewVideoView` remains attached only to `_previewPlayer`;
- the scrub Media is created once per PlayerWindow and reused between drags;
- the scrub overlay stays collapsed until `_scrubPlayer` raises `Playing`;
- when Playing arrives, the newest requested scrub target is applied and the
  overlay is made visible;
- while dragging, latest-position-wins seeking targets ~30 Hz and backs off to
  ~15 Hz when the decoder falls behind;
- releasing the timeline hides/stops only the scrub decoder and performs one
  final exact seek on the main player;
- hover-preview behavior is no longer disturbed by scrubbing;
- hardware decode preference remains enabled through `:avcodec-hw=any`.

New logs use `Timeline scrub decoder ...` so scrub-player startup, lag, and
errors are clearly separated from hover-preview logs.


## v30.40 scrub rendering on the main VideoView

v30.39 proved the dedicated scrub decoder was seeking and decoding, but its
frames were not visible. The reason is WPF/native HWND airspace: LibVLCSharp's
`VideoView` uses a native child window, and a second VideoView cannot be
reliably layered above the first with normal WPF `Panel.ZIndex`.

v30.40 removes the second full-size scrub VideoView entirely.

During timeline dragging:

- the dedicated `_scrubPlayer` is temporarily attached to the existing main
  `VideoView`;
- the normal `_player` remains paused in the background;
- decoded scrub frames therefore render on the exact same native surface the
  user is already looking at;
- latest-position-wins seeking and adaptive ~30/~15 Hz pacing remain;
- hardware decode preference remains enabled;
- on mouse release, `_scrubPlayer` is stopped, the main `VideoView` is handed
  back to `_player`, and one final exact seek is performed on the main player.

This avoids native-window z-order/airspace issues entirely.


## v30.41 smoother startup progress reporting

The splash previously appeared to sit around 3% because several early startup
operations occurred between only two coarse progress reports. v30.41 adds
visible milestones and dispatcher yields around the early initialization work.

The splash now reports separate stages for:

- process/WPF startup;
- loading native libVLC libraries;
- video-engine initialization;
- application-service creation;
- MainWindow construction;
- metadata loading;
- recording-service initialization;
- recording settings/root discovery;
- Steam app-name discovery;
- recording discovery;
- actual per-clip loading;
- filter/browser construction.

The expensive recording scan remains based on actual processed/total clip
counts rather than artificial timing.

Early-stage dispatcher yields let WPF repaint the splash between milestones, so
a real operation taking a second or two no longer leaves the progress display
visually stuck at the first percentage.

Steam app-name discovery also logs its elapsed time, which will make any
remaining startup stall easier to identify from SteamRecordingBrowser.log.


## v30.42 in-place main-player scrubbing

Timeline scrubbing no longer creates, swaps, or attaches any secondary
MediaPlayer to the main VideoView.

The previous dedicated scrub-player approach could cause libVLC to create a
separate native window titled `direct3d11 output`. That is not appropriate for
the embedded player UI.

v30.42 uses only the existing main `_player`, which permanently owns the
existing `VideoView`.

During a timeline drag:

- the main player is temporarily muted;
- if it was paused, it is briefly put into active decoding mode;
- latest-position-wins seeks are applied directly to `_player.Time`;
- the decoded frame therefore appears in the existing video player;
- no VideoView ownership is changed and no additional video-output window can
  be created by the scrub feature;
- seek pacing targets about 30 updates/sec while caught up and backs off when
  fragmented-DASH decoding lags.

On release:

- one final exact timestamp is applied;
- if playback was running before the drag, playback continues and the prior
  mute state is restored;
- if playback was paused before the drag, the decoder gets a short 90 ms settle
  window to present the selected frame, then pauses/freeze at that timestamp;
- the prior mute state is restored.

The normal timeline-hover preview player remains separate and unchanged.


## v30.43 compile cleanup

v30.43 fixes compile errors left behind by the v30.41 startup-progress and
v30.42 scrub simplification changes.

- `Dispatcher.Yield(...)` calls are now fully qualified as
  `System.Windows.Threading.Dispatcher.Yield(...)`.
- stale `_scrubPausePollTimer` constructor/event references were removed;
- stale `_scrubPausePollTimer` declaration/stop calls were removed if present;
- unused `_scrubReleasePosition` state was removed.

The v30.42 single-main-player scrub architecture is unchanged.


## v30.44 freeze while holding the timeline

Timeline drag scrubbing now freezes between requested frames instead of letting
the video continue playing while the left mouse button is held.

For each drag movement:

- the existing main player is briefly awakened if it is paused;
- the newest requested timestamp is applied;
- the decoder gets about 55 ms to present that frame;
- the player pauses again automatically;
- if the mouse stops moving while still held down, the visible video remains
  frozen on the last requested frame;
- moving again wakes the same player, seeks to the new location, and freezes
  again.

On release, the hold-freeze timer is stopped, the decoder is awakened for one
final exact seek, and the player's pre-scrub play/pause state is restored.

No secondary scrub MediaPlayer or additional video output window is used.


## v30.45 exclusive timeline-scrub input mode

Two older convenience features could compete with timeline scrubbing:

1. the global native video-click play/pause shortcut remained active while the
   timeline was capturing the mouse;
2. several scrub event paths were independently waking the main player, while
   asynchronous DASH seeks could later transition libVLC back to Playing.

v30.45 makes timeline scrubbing an exclusive player state.

While the timeline owns the left mouse button:

- `TogglePlayback(...)` ignores Space, the Play/Pause button, video-click
  toggles, and timeline-Space requests;
- the low-level native mouse hook explicitly excludes the timeline rectangle
  and any active timeline capture;
- only `PumpScrubFrame` is allowed to briefly wake the decoder;
- each requested frame gets a short ~60 ms decode window;
- a 20 ms scrub watchdog enforces Paused outside that decode window;
- `Player_Playing` also immediately suppresses unexpected asynchronous Playing
  transitions while a drag is active;
- mouse movement only updates the requested scrub target; it does not directly
  unpause the player.

This prevents a held timeline drag from unexpectedly becoming normal playback,
and prevents video-click play/pause behavior from stealing a timeline gesture.
