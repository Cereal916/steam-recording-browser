# Third-Party Notices

Steam Recording Browser includes third-party software in its source and
portable release packages. Each component remains subject to its own license.

## LibVLCSharp.WPF 3.10.1

Copyright © VideoLAN, VideoLabs, and LibVLCSharp contributors.

LibVLCSharp is licensed under the GNU Lesser General Public License,
version 2.1 (LGPL-2.1). Source code and the complete license are available at:

- https://github.com/videolan/libvlcsharp
- https://github.com/videolan/libvlcsharp/blob/3.x/LICENSE

## VideoLAN.LibVLC.Windows 3.0.23.1

Copyright © the VideoLAN project and contributors.

The portable distribution bundles the official Windows libVLC runtime.
libVLC is primarily licensed under LGPL-2.1-or-later. Individual modules may
carry compatible licenses documented by the VideoLAN project. Source code,
license texts, and legal information are available at:

- https://www.videolan.org/vlc/libvlc.html
- https://code.videolan.org/videolan/vlc
- https://www.videolan.org/legal.html

Steam Recording Browser does not modify the bundled libVLC binaries. The
libraries and plugins remain separate files in the portable distribution.

## .NET

The self-contained release includes components of the Microsoft .NET runtime,
which are licensed by Microsoft under their respective open-source licenses.
Source and license information are available at:

- https://github.com/dotnet/runtime
- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## FFmpeg 8.1 GPL build

Production release packages include `ffmpeg.exe` and `ffprobe.exe` from the
BtbN Windows x64 GPL static build. Steam Recording Browser invokes these as
separate, replaceable programs for transcoding and output validation. FFmpeg
and the optional libraries enabled in this build remain under the GNU GPL and
their respective compatible licenses; they are not covered by this project's
MIT license.

Each portable release includes `ffmpeg/SOURCE-AND-LICENSE.txt` with the exact
binary URL, verified SHA-256 hash, build identification, corresponding-source
locations, and license information. Upstream resources:

- https://ffmpeg.org/
- https://ffmpeg.org/legal.html
- https://github.com/FFmpeg/FFmpeg/tree/n8.1
- https://github.com/BtbN/FFmpeg-Builds

This notice is provided for attribution and license compliance. It does not
change the MIT license that applies to Steam Recording Browser's own source.
