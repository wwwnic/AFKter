# Task 2 brief

Read AGENTS.md and its relevant linked guidance, the feature spec `.scratch/agent-diagnostics/spec.html`, and ADR `docs/adr/0001-agent-diagnostics-use-bounded-history-and-read-api.md`.

Source ticket: .scratch/agent-diagnostics/issues/02-query-retained-history-across-restarts.html

02 — Query retained history across restarts and failures

Status

ready-for-agent

Blocked by

01 — Query live app and connection diagnostics

Summary

An agent can inspect persisted evidence after a restart, discover retained runs, and query an ordered history combining current memory and disk records without duplicates. History survives process exit within both the seven-day and 20 MB limits, and the agent can distinguish evidence loss or unavailability from an empty result.

Agent diagnostics design

·

Ticket index and review workflow

Acceptance criteria

Persist structured JSON Lines records asynchronously and retrieve retained run metadata and prior-run events through the authenticated API. Merge current memory and persisted records by run ID/sequence with stable ordering and no duplicates; do not present an old snapshot as current live state.

Enforce a total budget of 20,000,000 actual encoded bytes across all app-managed history files, including every retained run, active segment, and damaged leftover. Remove oldest evidence before a write would exceed the budget and re-establish the same accounting after restart.

Retain no queryable events older than seven days. Clean up at startup, before writes/history queries, and periodically while running; remove expired evidence at next startup after offline time. Retention eviction, including newer events removed with a mixed-age segment, is visible as a history gap.

If cleanup cannot maintain the byte budget, expose degraded persistence and stop writes that would violate the limit. Bound buffers, record sizes, retries/backoff, and failure metadata. Truncate oversized safe detail with explicit metadata; repeated failures cannot create an event storm or an unbounded queue.

Queries identify run/cursor, available sequence bounds, more-results/truncation, and known gaps or unavailable storage. An empty tail is distinguishable from expiry, eviction, dropped events, corruption, or loss that can be established. Promise crash survival only for successfully persisted records; never report an incomplete crash history as complete.

Recover complete previous-run records after abrupt exit and tolerate corrupt records or an incomplete final line without crashing the app. Record safe corruption/gap information, and do not synthesize a clean shutdown or a known acceptance outcome.

Coordinate storage ownership across concurrent instances so shared segments and retention accounting cannot be corrupted. Instances unable to obtain ownership continue app operation with explicitly degraded persistence; per-instance discovery remains distinct and stale ownership cannot permanently prevent recovery.

Linux tests use temporary directories and controllable time to verify both retention limits, multibyte byte accounting, oversized records, prior-run queries, memory/disk merging, incomplete/corrupt data, failed deletion, concurrent ownership, bounded retries, gaps, and recovery. Slow, full, locked, or unwritable storage must not change existing acceptance/cancellation or exceed the shutdown budget.

Document offline access, data lifetime and size semantics, completeness limits, and recovery troubleshooting. Keep the existing suite green, complete a separate subagent code review, and record the Windows filesystem checks still requiring native validation.

Comments

2026-11-07

— Published from the approved four-ticket breakdown. Complete the ticket-specific subagent review and, after all four tickets are integrated, a fresh combined review. No product implementation is included in ticket publication.

## Execution contract
Implement this ticket only. Follow TDD, demonstrate focused RED before implementation and GREEN after. Run the full suite once before committing; verify Windows Release cross-build. No subagents, no push/merge/branch switches. Commit scoped work on the existing feature branch. Keep source files focused. Update this ticket comments with results but do not invent a done label. Full report must include public interfaces for the next tickets, finite limits, TDD/test evidence, commits, and concerns.

Tooling: /workspace/.dotnet/dotnet with DOTNET_CLI_HOME=/workspace/.dotnet-home NUGET_PACKAGES=/workspace/.nuget/packages MSBuildEnableWorkloadResolver=false. Full test: dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true; build: dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true.

## Established ticket 01 interfaces
Read task-1-report.md in this scratch directory for exact public contracts and limits; inspect current source before extending. Core owns DiagnosticCollector and immutable contracts; the separate AFKtor.Diagnostics project owns Kestrel/runtime/discovery. DiagnosticObservation currently permits enum/numeric data only. IDiagnosticSink.WriteAsync is isolated and gets 500ms; collector disables a sink that fails once. IDiagnosticHistory.GetRuns/GetEvents is the host read seam. Add a cancellation-aware bounded history provider/merger and deliberate safe persistence-health/retry seams; do not couple source-specific event codes to storage. Extend documentation and tests with actual selected finite storage/retry/segment limits. Ticket 01's generic data envelope must stay compatible with future typed evidence additions.
