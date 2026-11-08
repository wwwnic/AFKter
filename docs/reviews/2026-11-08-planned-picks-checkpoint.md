# Planned picks — one-ticket checkpoint

The player authorized exactly one additional implement-spec ticket. Ticket 04
adds planned pick hover/lock, manual takeover and the default deadline watchdog.
Tickets 05–07 remain untouched; work pauses after commit and push.

Commit `a1d46fc` implements the pick slice. The assigned role's latest ordered
plan supplies eligible candidates; normal timing hovers before locking. The urgent
path skips avoidable waits. Manual changes pause normal selection for the draft;
the default watchdog attempts to lock only the current valid hover, including
champions outside the plan. Invalid hovers produce no substitution. Live plan
edits may replace an automatic hover and do not clear manual takeover.

A fresh read-only reviewer identified two P2 defects: final verification's shorter
timer was not persisted into the monotonic deadline, and completed-pick evidence
could retain a prior hover instead of the actually observed completed champion.
A fresh implementer added six regressions, watched them fail, and fixed both in
`f8eed5f`. Targeted re-review approved the corrections with no new material
finding. Merge `c816fe9` includes the reviewed fixes on `feature/draft-companion`.

Integration verification used SDK 10.0.403:

```sh
dotnet test AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
git diff --check bc58c10..HEAD
```

All **255 tests passed**, none failed or skipped; the prior baseline was 215.
The Release Windows cross-build had **zero warnings and errors**. Whitespace
validation passed. Tests include deterministic clocks, edit/dispatch races,
manual takeover, urgent/normal flows, eligibility across both teams, ambiguous
outcomes, cancellation, diagnostics and simulated HTTP monitor integration.

Both shipped native-readiness gates remain **false**. This checkpoint enables
no live draft writes. Windows UI and installed-League semantics remain unverified
on this Linux host. Native enablement requires the evidence and checks in
[planned picks](../planned-picks.md), retaining all prior observer and ban gates.
Polling cadence and sampled hover attribution are documented best-effort limits.
Configurable watchdog policies and advanced timing are outside this ticket.
