# ADR 0005: Maintain UI consistency and AI-assisted contribution standards

- Status: Accepted
- Date: 2026-08-23

## Context

Steam Recording Browser uses a cohesive dark visual design across its windows,
dialogs, controls, popups, and native Windows surfaces. New UI added without
following that design makes the application feel inconsistent and can reduce
readability.

The project is also developed with AI assistance. Code changes need a concise,
ready-to-use commit message so they can be reviewed and committed consistently.

## Decision

- Every new UI feature must follow the application's established dark-mode
  styling, including its backgrounds, foregrounds, borders, control states,
  spacing, and Steam-blue accent colors.
- New or modified controls must include polished dark-mode hover, pressed,
  focused, selected, disabled, popup, and tooltip states wherever those states
  apply.
- Existing shared styles and visual patterns should be reused before adding new
  one-off styling.
- After making any source-code change, an AI assistant must end its final
  response with a concise commit message that accurately summarizes the change.
- The commit message must be the final line of the response and use the format
  `Commit message: <message>`.

### Commit-message format

Commit messages must follow these conventions:

- Use the Conventional Commits subject format: `type(scope): summary`. The
  scope is optional and should only be included when it adds useful context.
- Choose the type that best describes the change, such as `feat`, `fix`,
  `docs`, `test`, `refactor`, `perf`, `build`, `ci`, or `chore`.
- Write the summary in the imperative mood, keep it lowercase unless a proper
  name requires capitalization, and do not end it with a period.
- Keep the subject concise—preferably no longer than 72 characters—while still
  naming the user-visible behavior or important technical outcome.
- Do not use vague subjects such as `update files`, `make changes`, or
  `misc fixes`.
- When a change contains multiple meaningful features, fixes, migrations, or
  release details that cannot be represented accurately in one concise subject,
  include a short body after a blank line.
- Keep body entries focused on important behavior, compatibility, migration,
  security, or verification details. Do not repeat the subject or enumerate
  trivial implementation steps.
- For a multi-line recommendation, the final response must use a fenced text
  block immediately after `Commit message:` so the complete message can be
  copied without reformatting.

Examples:

`fix(player): restore startup audio and preserve volume state`

```text
feat(player): improve long-video navigation

- add fine scrubbing and exact timestamp seeking
- add duration-aware timeline markers and seek controls
- reduce DASH seek pressure for recordings longer than one hour
```

## Consequences

New interfaces remain visually consistent with the rest of the application,
including less frequently used dialogs and interaction states. AI-assisted
changes also arrive with a predictable commit-message handoff, reducing effort
when reviewing and committing completed work.
