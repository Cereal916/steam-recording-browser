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
- Selector controls must use a complete dark template for both the closed
  control and popup items. A custom `ComboBox` must explicitly give its
  internal `ToggleButton` a transparent template so native Windows hover
  rendering cannot place a light surface over light text.
- Selector changes must be checked in their normal, hovered, focused, open,
  highlighted, selected, and disabled states. New selectors must reuse the
  established application selector template instead of the native WPF
  `ComboBox` template or a partially styled replacement.
- Buttons must inherit the application-level dark `Button` template rather
  than relying on the native WPF template. Verify readable foreground and dark
  backgrounds in normal, hovered, pressed, keyboard-focused, default, and
  disabled states. Window-specific button styles may adjust spacing or colors,
  but must be based on the shared application style unless they provide an
  equally complete dark control template.
- Checkboxes must inherit the application-level dark `CheckBox` template so
  unchecked, checked, hovered, keyboard-focused, and disabled states remain
  readable and do not fall back to native light Windows chrome.
- Context menus must inherit the application-level dark `ContextMenu`,
  `MenuItem`, and `Separator` templates. Verify the popup surface, borders,
  item text, icons, gesture text, separators, hover, keyboard focus, and
  disabled states instead of relying on native light Windows menu chrome.
- Scrollable controls must inherit the complete application-level dark
  `ScrollViewer` and `ScrollBar` templates. Styling only the track or thumb is
  insufficient: the line/page buttons, horizontal and vertical tracks, and
  the bottom-right intersection where both scrollbars meet must all have
  explicit dark surfaces so WPF cannot display native light chrome.
- Scrollbar verification must cover vertical-only, horizontal-only, and
  simultaneous two-axis overflow. Check the full top, bottom, left, and right
  edges—including the bottom-right corner—in non-maximized windows, at the
  supported minimum size, and with display scaling enabled.
- Text inside padded cards, borders, dialogs, popups, and tooltips must be
  verified at its actual rendered size, including the final wrapped line.
  Prefer `Auto` sizing or content-sized dialogs when text can wrap; do not put
  variable-height text in a constrained fixed or star-sized row that can clip
  descenders or hide the bottom line behind container padding.
- UI layout verification must include long or wrapping copy, non-maximized
  windows, and display scaling. A feature is not complete if text overlaps,
  clips, or is obscured by padding at any supported minimum window size.
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
