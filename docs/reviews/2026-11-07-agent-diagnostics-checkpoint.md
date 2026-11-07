# Agent diagnostics checkpoint before ticket 03

The user paused execution before ticket 03 and requested that current work be
committed and pushed to `codex/agent-diagnostics`. Tickets 03 and 04 have not been
started. No merge into `main` or pull request is included in this checkpoint.

## Status

| Diagnostics ticket | Implementation | Review |
| --- | --- | --- |
| 01 — Live app/connection queries | `422e252`, test correction `d0e448a` | Accepted after independent review and scoped fix review |
| 02 — Retained history/recovery | `3c57afd`, report `b090dec`, correction `64a8c98` | Important finding corrected and accepted by scoped review; one deferred Minor below |
| 03 — Ready-check decision evidence | Not started | Not started |
| 04 — Startup/settings/LCU failure evidence | Not started | Not started |

Ticket 01 adds the authenticated loopback Kestrel API, restricted per-instance
local discovery, coherent live state and bounded current-run events. Core remains
independent of ASP.NET and WinForms. Ticket 02 adds bounded JSONL history,
previous-run queries, memory/disk merging, retention, corruption/crash recovery,
exclusive storage ownership and degraded-health handling.

## Review findings and corrections

The independent ticket 01 review passed spec compliance and found a test-only
portability defect: a `Parallel.For` assertion assumed contention would occur.
It failed on a single-CPU runner. The correction creates held-lock contention
explicitly, and focused isolation tests pass 4/4 with both default processor
settings and `DOTNET_PROCESSOR_COUNT=1`. Scoped review accepted the fix.

The independent ticket 02 review found a completeness defect. After the collector
disabled a failed/timed-out sink, the underlying store could recover while writes
remained disabled. Queries could claim available persistence and hide a known
hole between disk and memory when requesting an empty tail or an intact early
page. Correction `64a8c98` preserves the sink-disabled state, exposes queue loss,
and checks continuity across the bounded retained union independently of the
requested page. The regression failed first, then passed with the correction. Scoped independent re-review accepted the original finding as addressed and found no new blocking issue.

One Minor follow-up remains: when the write sink is disabled but the history store
is readable, an unknown prior-run query may return HTTP 503 `storageUnavailable`
instead of HTTP 404 `runUnavailable`. Known retained runs remain queryable. Read
and write availability should be separated for that error classification and
covered by a focused test. This is explicitly deferred at the user-requested
pause and must be considered in the later combined review.

## Final automated evidence for the checkpoint code

At source commit `64a8c98`:

- 20 focused history tests passed, with no failures or skips.
- Full solution: 127 tests passed, with no failures or skips.
- Windows Release cross-build: zero warnings and zero errors.
- Diff whitespace checks passed.

Commands use .NET SDK 10.0.400:

```sh
dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
```

The tracked [ticket 02 implementation report](../../.superpowers/sdd/agent-diagnostics/task-2-report.md)
records the interfaces, finite limits, RED/GREEN evidence and operational caveats.
Its tracked evidence files are delivery artifacts; do not blindly delete the
whole execution directory as disposable scratch. Other local orchestration files
in that directory may remain git-ignored.

## Remaining validation and limitations

Native Windows startup, effective discovery ACLs, runtime packaging, NTFS ownership
and crash release, deletion/replacement, denied/full/slow filesystems, and installed
League behavior remain unverified. Linux tests and Windows cross-compilation do
not establish these runtime checks as passed.

A sink exceeding 500 ms remains disabled for that run. Indexed history reads may
recover, but lost writes are not replayed and persistence must remain visibly
unavailable for the current writer. Direct standalone store disposal can wait on
an uninterruptible filesystem call; application composition enforces the shared
two-second diagnostic shutdown budget. Initial unindexed-file reclamation uses
filesystem modification time, and exact lost crash tails or identities beyond
bounded tombstones cannot always be established. These limits remain explicit.

## Resume instructions

1. Respect the pause: do not start tickets 03 or 04 until the user asks to resume.
2. Read the [design](../../.scratch/agent-diagnostics/spec.html),
   [ticket index](../../.scratch/agent-diagnostics/ticket-breakdown.html),
   [execution plan](../superpowers/plans/2026-11-07-agent-diagnostics.md),
   [ADR](../adr/0001-agent-diagnostics-use-bounded-history-and-read-api.md), and
   [API contract](../agent-diagnostics.md).
3. Continue from git history and this status; do not reimplement accepted ticket 01.
   Check ticket 02's final review status before proceeding.
4. Subsequent implementations each need their own fresh subagent review. When all
   four tickets are integrated, assign a fresh combined reviewer and rerun the
   full tests/build. That final whole-feature review has not happened yet.
5. Source observations emitted after releasing controller locks can arrive out of
   order; ticket 03 must preserve coherent snapshots and decision-time identities.

## Coordination choices

Implementation was sequential in the shared checkout to avoid conflicting edits,
at the cost of elapsed time. The accepted tickets/spec authorized implementation;
the execution plan recorded coordination without another approval gate. The one deferred Minor is recorded above; no blocking review finding was silently
parked or waived at this checkpoint.
