# Planned bans — one-ticket checkpoint

The player authorized exactly one additional implementation ticket, followed by
commit, push and pause. Ticket 03 was selected because its prerequisites, tickets
01 and 02, are implemented. Tickets 04–07 remain untouched.

Implementation commit `dc478b6` adds ordered planned bans, teammate play-intent
protection, current eligibility/revision verification, serialized dispatch,
manual takeover, request-outcome reconciliation, normal/urgent pacing,
cancellation, tray status and bounded diagnostics. Merge `595e417` integrates it
on `feature/draft-companion`.

A fresh read-only reviewer checked the complete change against the approved
specification, ticket, repository guidance and diagnostic ADR. No material
findings remained. The review approved the gated implementation and explicitly
left native enablement pending; it did not independently rerun tests.

Integration verification ran with SDK 10.0.403:

```sh
dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
git diff --check 1cdb2bb..HEAD
```

All **215 tests passed**, including 25 new ban cases. The Release Windows
cross-build had **zero warnings and errors**. Whitespace validation passed.
Tests exercise the real HTTP adapter with simulated League responses, including
the full monitor-to-dispatch-to-observed-completion path with the gate on/off.

The shipped native-readiness gate remains **false**, as required by the approved
specification until installed-client semantics are verified. The tray reports
that status. No live automatic bans are enabled by this checkpoint. Native
Windows/League validation is unavailable on this Linux host. See
[planned-ban evidence and enablement checklist](../planned-bans.md) for exact
remaining gates and documented polling/attribution limits.

Work pauses after committing and pushing this checkpoint. Starting another
ticket requires a subsequent instruction from the player.
