# ADR 0001: Use WPF, .NET 10, and LibVLCSharp

- Status: Accepted
- Date: 2026-08-22

## Context

Steam Recording Browser is a Windows-only desktop application that needs native
Windows UI integration and reliable playback of Steam Game Recording DASH
content.

## Decision

Use:

- C#
- WPF
- .NET 10
- LibVLCSharp.WPF
- bundled VideoLAN libVLC

## Consequences

The application has strong Windows integration and access to libVLC's native
multimedia capabilities. Production builds remain Windows-specific and include
native libVLC binaries and plugins.
