# Draft companion — two-ticket checkpoint

Tickets 01 (read-only draft observation) and 02 (live role-plan editor and
autosave) are implemented on `feature/draft-companion`. The player explicitly
limited this batch to two tickets to review usage before continuing. Tickets
03–07 have not started. No draft pick or ban requests are implemented.

The observer exposes queue, assigned role, action window and conservative
admission status in the tray and existing bounded diagnostics. It uses serialized
read-only LCU requests and synthetic replay fixtures. The editor supports all
five roles, ordered pick/ban lists, immutable immediate revisions, background
atomic autosave, visible failure/retry, and validated restart recovery. Its
bundled official Riot champion identity catalog supports offline use.

## Review and correction

A fresh read-only reviewer checked the combined implementation against both
tickets and the approved design. One P2 admission race was reproduced: an idle
read started before enabling could complete afterward and admit an ongoing
draft. Commit `1b17475` fences reads with an arming epoch. Both initial-enable
and disarm/rearm held-read regression cases failed before the fix and pass now.
The reviewer approved the targeted correction and independently passed the four
monitor tests. No other material finding remained. Review deliberately leaves
later pick/ban execution outside this batch and native checks pending.

## Verification

The integration merge `b652233` contains both tickets and the reviewed fix.
With SDK 10.0.403, integration verification ran:

```sh
dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
git diff --check
```

All **190 tests passed**, none failed or skipped. The Release Windows
cross-build had **zero warnings and errors**. Whitespace validation passed.
The pre-change baseline had 151 passing tests.

## Validation limits and handoff

Native Windows UI, DPI/keyboard behavior, Windows filesystem permissions, and
installed League lifecycle/timer semantics have not been verified on this Linux
host. Published schemas and synthetic fixtures do not establish installed-client
compatibility. Follow the explicit checklists in [draft observation](../draft-observer.md)
and [role plans](../role-plans.md) before claiming native acceptance or enabling
future draft writes.

Ticket resolution fields record implementation separately from the canonical
triage labels. The spec and ticket index now record this checkpoint. Work pauses
here for the player's usage review; no further ticket is authorized in this batch.
