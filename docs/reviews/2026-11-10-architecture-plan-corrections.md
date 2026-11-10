# Avalonia architecture plan corrections

ASTRA (GPT-6-ASTRA, high reasoning) reviewed the current architecture and proposed Avalonia migration. Its recommendation was to retain Core, extract one application runtime, and proceed with the unified Avalonia shell without adding unnecessary project layers.

The corrections are documentation and future implementation contracts; the production app remains WinForms. No migration task was implemented in this change.

## Corrections recorded

- Identify a selected plan occurrence by role, list and champion ID, independently of the insertion destination. Toolbar commands use that selection; keyboard commands use the focused occurrence. A missing selection cannot silently bind to another occurrence of the same champion.
- Give the runtime one cached shutdown task shared by all Stop/Dispose callers. Define start/stop ordering and test held reads and the final real file save through explicit barriers.
- Give each window one presentation refresh owner, stopped while hidden and resumed on reopening without accumulating timers or subscriptions.
- Require new pointer and keyboard edits to use the planned atomic stable-ID edit boundary.
- Update README and AGENTS to describe current composition, diagnostics, metadata and legacy probe paths accurately. Planned projects remain explicitly planned.

The five-task dependency graph and needs-triage statuses are unchanged. Native draft-write gates remain closed.

## Review and resolution

The fresh ASTRA review of `22f6e87..f6ae322` found one remaining P2 issue: the proposed shutdown test could stop the runtime before its background read started, then wait indefinitely for cancellation of a nonexistent read.

Commit `079a6c6` establishes cadence-delay registration, advances the controlled clock, and waits for the held read to start before invoking concurrent shutdown. Cancellation-observed and final-save barriers remain in place. ASTRA's targeted re-review of `f6ae322..079a6c6` confirmed the race was resolved and found no new material issue in that narrow change.

## Validation

Local specification/ticket HTML and file links were checked, ticket dependencies and statuses were preserved, and plan checkboxes remain unchecked. `git diff --check` passed. Reviews were read-only source/documentation reviews; runtime tests, desktop interaction and League validation were not run for these documentation-only changes.
