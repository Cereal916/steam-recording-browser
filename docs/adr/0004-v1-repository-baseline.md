# ADR 0004: Establish the 1.0 repository and versioning baseline

- Status: Accepted
- Date: 2026-08-22

## Context

The application reached a stable feature set after a long internal development
period that used `30.x` iteration numbers.

The project is now being maintained as a normal GitHub repository and developed
primarily in JetBrains Rider.

## Decision

Beginning with the public production baseline:

- release version is `1.0.0`
- future releases follow semantic versioning
- source code lives under `src/`
- tests live under `tests/`
- release tooling lives under `eng/`
- architecture documentation and ADRs live under `docs/`
- shared Rider run configurations live under `.run/`
- CI runs on GitHub Actions for Windows
- package versions are centralized in `Directory.Packages.props`

Internal `30.x` build numbers remain only as historical changelog entries.

## Consequences

The repository has a stable production layout and a conventional public
versioning scheme suitable for GitHub releases and long-term maintenance.
