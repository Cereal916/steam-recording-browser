# ADR 0003: Portable self-contained Windows deployment

- Status: Accepted
- Date: 2026-08-22

## Context

The application depends on .NET and native libVLC binaries/plugins.

## Decision

Publish as a self-contained Windows x64 application and distribute the complete
published folder as a portable ZIP.

Do not require:

- a separate .NET runtime
- a separate VLC installation
- PowerShell on end-user systems

## Consequences

The release contains multiple native/runtime files. The published directory
must remain intact.
