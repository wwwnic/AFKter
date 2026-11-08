# Live timing — ticket 06 checkpoint

The player requested the next implement-spec task and authorized concurrent
unfinished dependencies. Ticket 06 was selected; its blockers, tickets 03 and 04,
were already implemented and reviewed, so no additional dependency work was
necessary. Ticket 07 has not started.

Commits `bd47a76` and `6b10695` implement bounded live timing settings, an initially
collapsed Advanced section, one-revision author-default reset, immediate effective
edits, and shared background autosave/retry. Existing saved ready-check delay is
imported when stored timing is absent. Current docs and tests explicitly replace
the original captured-delay contract: pending checks use original detection plus
the latest delay. Skipped/finished checks remain terminal; sent accepts are not
retried. Draft timing uses original action/hover clocks and monotonic deadlines.

A fresh read-only review found one P2: completion observed only in final pick
verification was not latched, allowing later stale active state plus a timing edit
to dispatch again. A fresh implementer added regressions and fixed it in `b73af22`.
Targeted re-review approved the correction; its independent reproduction now
remains Completed with zero writes after both observations. Integration merge
`c77d722` contains the reviewed fix on `feature/draft-companion`.

Integration verification used SDK 10.0.403:

```sh
dotnet test AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
git diff --check 5768370..HEAD
```

All **312 tests passed**, none failed or skipped; the baseline had 285 tests.
The Release Windows cross-build had **zero warnings and errors**. Whitespace
validation passed. Coverage includes original-clock live delay changes, held
verification, settings bounds, reset, migration, save failures, dispatch revisions,
monotonic expiry and terminal completed observations.

Both shipped draft-write gates remain false. Windows UI, keyboard/DPI, native
filesystem behavior and installed-League timing remain unverified on this Linux
host. See [live timing controls and native checks](../live-timing.md). Simulation
and cross-compilation do not establish live acceptance. The remaining ticket 07
requires actual native evidence and must report unavailable checks as blocked.
Work pauses after committing and pushing this checkpoint.
