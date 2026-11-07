# Ticket 02 implementation report

Status: DONE_WITH_CONCERNS — implementation and Linux verification complete; final command results and commit recorded below. Independent ticket review and native Windows filesystem checks remain pending. Tickets 03 and 04 were not started.

## Scope and composition

Resumed preserved uncommitted ticket02 implementation on `codex/agent-diagnostics`, base `d0e448a`; accepted ticket01 behavior remains the producer/HTTP foundation. Added JSONL persistence, restart recovery, retention accounting, exclusive directory ownership, cancellation-aware retained history reads, memory/disk merge, persistence health and default runtime/tray composition. All ownership, initialization, recovery, cleanup and durable writes execute on background diagnostic/API workers. Runtime construction performs no filesystem work. Normal shutdown drains the collector then releases owned storage, concurrently stopping the host under the existing shared two-second budget.

## Exact public contracts for subsequent tickets

Core namespace `AFKtor.Core.Diagnostics`:

- Existing `DiagnosticCollector.Publish(DiagnosticObservation)` / `ConnectionChanged` remain the sealed nonblocking source seam. Producers still accept fixed enums and numeric generation, with no callback, formatting or I/O on producers.
- Added `DiagnosticCollector.GetMemoryHistory(): DiagnosticEventPage`, a coherent bounded copy of the entire 1,024-event memory ring. Unlike HTTP pages this internal-composition read can contain all retained memory events; do not expose it as an unbounded HTTP page.
- `DiagnosticEvent` adds optional `bool Truncated = false`.
- `DiagnosticHealth` adds optional `DiagnosticPersistenceStatus? Persistence = null`.
- `DiagnosticEventPage` adds optional `string[]? Gaps = null`, `string Storage = "unavailable"`, `long? PersistedThroughSequence = null`; prior constructor calls remain compatible.
- `IDiagnosticHistory` retains synchronous `GetRuns()` and `GetEvents(Guid,long,int)` and adds default cancellation-aware overloads `GetRuns(CancellationToken)` and `GetEvents(Guid,long,int,CancellationToken)`. Implement blocking providers with these overloads; HTTP passes its request/lifetime deadline.
- Added `DiagnosticPersistenceStatus(string Availability, string? FailureCategory, long Dropped, long Truncated, long DeletedSegments, string[] Gaps)` and `IDiagnosticSinkHealth.Status`. The getter must return immediately. The collector samples injected sink health only inside isolated background work and bounds/allowlists cached categories; producer/state access invokes no sink callback.
- `IDiagnosticSink.WriteAsync(DiagnosticEvent,CancellationToken)` remains the durable-write seam. A sink call is limited to 500ms, gets a linked deadline token, and a throwing/timed-out sink remains disabled for that run. Storage-handled transient failures return fixed degraded health rather than throwing and use the store's own bounded backoff.

Diagnostics namespace `AFKtor.Diagnostics`:

- `DiagnosticHistoryStore(string directory, TimeProvider? timeProvider = null)`: sealed `IDiagnosticSink`, `IDiagnosticHistory`, `IDiagnosticSinkHealth`, `IAsyncDisposable`. Constructor is memory-only and schedules maintenance; the first background write/read performs ownership/recovery. `Status` is an immediate immutable snapshot. Both history methods have cancellation-aware overloads.
- `RetainedDiagnosticHistory(DiagnosticCollector collector, DiagnosticHistoryStore store, TimeProvider? timeProvider = null)`: sealed `IDiagnosticHistory`, `IDiagnosticSinkHealth`; current memory wins duplicate `(runId,sequence)` records. `Status` forwards the immediate store snapshot.
- `DiagnosticRuntime.Start(string discoveryDirectory, IDiagnosticSink? sink = null, IDiagnosticHistory? history = null, string? historyDirectory = null)` remains nonblocking. With no supplied sink it owns a default store and merger. Default injected path is `<discoveryDirectory>/history`; the tray explicitly passes `%LOCALAPPDATA%/AFKtor/diagnostics/history`, keeping discovery separate. Supplied sink/history lifetimes remain caller-owned.
- Existing `DiagnosticHost.StartAsync(...)` shape is unchanged. Unknown-run responses are 503 `storageUnavailable` when storage cannot establish existence, otherwise 404 `runUnavailable`. API state overlays current sealed-store health, including idle retention cleanup, without I/O. A disabled sink remains unavailable even if indexed storage reads later recover.

`DiagnosticHistoryStore.RunMetadata` is a public nested serializer DTO, not a source publishing seam. It contains RunId, bounded Build, StartedAt, Watermark, Dropped and the fixed gap set. No store-owned instance is exposed.

## Finite limits and file semantics

- 20,000,000 actual encoded bytes across every regular file in the dedicated history directory, including active/recovered/damaged segments, metadata.json, metadata.tmp, unknown leftovers and owner.lock. Do not place unrelated files in the directory: cleanup can remove them.
- New JSONL segments rotate at 65,536 bytes; each event including newline is at most 2,048 bytes. Text is bounded before JSON serialization (Build/Code128 chars, Source64, Severity32), with explicit truncation; a bounded output stream and fixed fallback enforce encoded size for multibyte text. Later typed evidence must retain its safe bounded schema.
- At most512 retained indexed segments, 32,768 indexed events, 128 run identities/tombstones. Recovery temporarily examines one incoming segment before trimming; its list is bounded to 513 segment objects. Event indexing evicts before adding beyond 32,768 records. These limits are subordinate to the total byte budget and may evict earlier.
- Metadata files each at most 262,144 bytes; cleanup reserves 524,288 replacement bytes conservatively before event writes. Old and replacement manifests both count. Oversized damaged manifests are reclaimed with a corruption gap. Inventory rejects more than 1,024 file entries rather than allocating indefinitely.
- Seven-day event lifetime. Cleanup runs on initialization, before writes/history reads and every minute. Queries filter by age even if deletion fails. Expired mixed-age segments are removed whole and loss remains visible. Run tombstones can outlive their events but are bounded to 128 identities.
- Retry delays 1, 2, 4, 8, 16, 32, 60 seconds, capped at 60; one serialized attempt after the deadline. Intervening writes increment dropped counters without I/O retries. Five fixed durable gap categories; no recursive health events or exception/path text.
- Existing bounds retained: 1,024 memory events, 256 sink queue entries, 1–100 HTTP page records, 262,144 response bytes, 8 isolated readers, 500ms sink deadline, shared 2s shutdown. A hung reader retains its slot; a hung sink can leave at most one abandoned operation. Filesystem calls that cannot be cancelled remain background tasks and never extend the runtime shutdown budget.

## Recovery, ordering, loss and completeness

Complete newline-terminated records are recovered and sorted by sequence within run. Invalid JSON/oversized lines/incomplete final lines are skipped and reported as corruption; no clean shutdown or known accept outcome is synthesized. New writes do not append to recovered segments. Each successful write flushes through the filesystem before indexing; crash survival is promised only for successfully persisted records, subject to hardware/filesystem durability. The manifest is replaced through a flushed temporary file.

Ownership is one exclusive owner.lock file handle per history directory; release/crash is handled by the OS. A concurrent instance keeps its own collector/API/discovery, reports ownershipUnavailable and retries with backoff. Lock-file presence alone is not ownership. All instances share the same byte accounting when ownership is acquired.

Memory/disk merge deduplicates by sequence with memory winning, filters memory expiry before pagination, then returns ordered bounded pages. Available bounds describe the union; nextCursor is the last returned sequence, and hasMore describes retained evidence after that cursor. The empty-tail regression ensures expired memory cannot hide newer records beyond a requested page. Metadata watermarks reveal known loss when recorded files disappear.

Prior-run completeness is persistedPrefixOnly or incomplete; current-run completeness is currentRunMemoryAndDisk or incomplete. Gaps are expired,evicted,corruption,unpersisted,sequenceGap,producerDropped,storageUnavailable. Store-wide gaps are deliberately conservative and can affect otherwise intact runs. PersistedThroughSequence is the greatest currently retained persisted sequence, not a proof of contiguous persistence or of a complete crash tail. Producer drops have no fabricated sequence. Collector queue losses and store write drops have separate health counters. Evicted tombstones eventually lose run identity, so unknown older identities cannot always be distinguished from never-seen runs.

## TDD and verification evidence

All commands use DOTNET_CLI_HOME=/workspace/.dotnet-home, NUGET_PACKAGES=/workspace/.nuget/packages, MSBuildEnableWorkloadResolver=false and `/workspace/.dotnet/dotnet`.

Preserved initial RED/GREEN logs remain in this directory. Initial store slice:7 tests GREEN in task-2-green-initial.log after the prior implementer's runtime RED. The preserved wiring test initially failed compilation due Unix permission platform guards; added an explicit return on Windows, then reproduced intended runtime RED (prior-run HTTP404), followed by 11/11 GREEN.

- task-2-wiring-red.log: intended prior-run404 failure after fixing CA1416 test guards.
- task-2-wiring-green.log:11 passed.
- task-2-merge-backoff-red.log:2 expected failures, expiry-prefix empty page and unavailable indexed read during backoff.
- task-2-merge-backoff-green.log:13 passed.
- task-2-recovery-red.log:2 expected failures, newest segment discarded at recovery index bound and unavailable storage returned404. Periodic expiry already passed as characterization.
- task-2-recovery-green.log:14 passed,2 failed from test timing assumptions (old disposed manual timer and nonblocking runtime health still starting). Fixed test synchronization to await the active timer and settled asynchronous health; did not block runtime startup.
- task-2-gap-budget-red.log:2 expected failures, deleted-file known gap absent and oversized damaged manifest unreclaimed. Full memory/disk pagination passed as characterization.
- task-2-focused-final.log:19 passed,0 failed,0 skipped.
- task-2-full-test.log: 126 passed, 0 failed, 0 skipped (final corrected source).
- task-2-build.log: Windows Release cross-build succeeded, 0 warnings, 0 errors (37.28s).
- Final git diff --check: clean.

Commands: `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true`; `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`.

The full suite was repeated after a final self-review fix that bounds strings before JSON serialization and limits injected health-array inspection. An intermediate compile-only rerun caught and corrected a duplicated local introduced while applying that final small edit. Final results above refer to the corrected source, not the intermediate compile failure.

## Self-review and remaining concerns

Reviewed all new storage/recovery/retention/merger files and the changed Core/host/runtime/tray contracts; checked byte accounting before writes, replacement reservation, deletion failure, bounded indexing/metadata/retry behavior, cancellation propagation, startup/shutdown isolation, and coherent memory pagination. No ticket03/04 implementation, branch switch, push, merge or subagent execution occurred. Root will dispatch the required fresh reviewer and handle push.

- Independent ticket review is pending; this report does not claim that gate passed.
- Native Windows NTFS sharing/ownership/crash release, open-file deletion/replacement, inherited history ACLs, denied access/full volume, slow-filesystem shutdown and packaging remain unverified. Linux permission tests and a Release cross-build cannot establish native Windows behavior.
- A sink exceeding 500ms is deliberately disabled for the remainder of the run, even if storage later becomes healthy. Retention maintenance can recover indexed reads; it does not replay lost writes or re-enable a disabled collector sink. Drop/failure metadata makes this degradation visible.
- Direct store disposal alone can wait for a non-cancellable filesystem operation; application composition wraps it in the shared 2s budget. The runtime, not a directly awaited standalone store, is the bounded application lifetime seam.
- Initial cleanup treats unindexed/unknown files by filesystem modification time; after recovery indexed segments are ordered by oldest event timestamp. External timestamp changes can therefore affect which unindexed segment is reclaimed first.
- Exact crash-tail losses cannot always be established. Conservative global gaps and bounded tombstones intentionally avoid false completeness.
- Full-volume and arbitrarily stalled native filesystems are not manufactured in Linux tests. Real unwritable paths, denied deletion, lock contention, corruption, backoff and existing hung sink/reader isolation regressions cover the automated failure boundaries.

## Commits

`3c57afd` — Retain bounded diagnostic history across restarts — scoped ticket02 source/tests/docs/ticket comment and durable execution report/evidence. Base d0e448a. No push performed by this agent.

This report is saved in a following documentation-only commit, so it can name the implementation commit without a self-referential hash. Resolve its own commit with `git log -1 --format=%H -- .superpowers/sdd/agent-diagnostics/task-2-report.md`.


## Review fix round 1 — disabled sink and page-independent gaps

The fresh review (`task-2-review.md`) found one Important correctness issue: a collector-disabled sink could be concealed after the underlying store became healthy, and a missing interval between retained disk and memory could disappear from an empty tail or an intact early page. Reproduced before the fix in `DiagnosticHistoryTests.DisabledSinkCannotClaimRecoveredPersistenceOrHideUnionGapOnAnyPage`: an actual sink call outlives 500ms, disk independently retains 1–100, and memory then rolls to 1000–2023. The test checks availability before memory eviction, then authenticated early/tail pages, run metadata, state and its persistence health; producer drops remain zero and no sequence positions are fabricated for sink loss.

Added fixed-cost public collector getters `bool SinkDisabled` (durable for the run) and `long SinkDropped`; no wire schema or producer behavior changed. The merger includes queue loss as `unpersisted` and keeps current-run persistence unavailable with `sinkUnavailable` after sink disablement, even if indexed reads recover. An internal store snapshot reuses its already bounded ordered array; a linear merge checks at most 32,768 disk + 1,024 memory records for union discontinuities independently of the requested page, without extra I/O or gap-check allocation. Prior-run reads and separate producer/store/queue loss counters retain their existing semantics. Updated the diagnostic contract documentation accordingly.

Commands use the same DOTNET_CLI_HOME/NUGET_PACKAGES/MSBuild environment documented above:

- RED: `dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj -p:EnableWindowsTargeting=true --filter FullyQualifiedName~DisabledSinkCannot` — 1 failed, with the expected availability/completeness/gap assertions (`task-2-fix1-red.log`).
- Focused GREEN: `dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj -p:EnableWindowsTargeting=true --filter FullyQualifiedName~DiagnosticHistoryTests` — 20 passed, 0 failed, 0 skipped (`task-2-fix1-focused.log`).
- Full suite: `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true` — 127 passed, 0 failed, 0 skipped (`task-2-fix1-full.log`).
- Windows Release: `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true` — succeeded, 0 warnings, 0 errors (33.03s) (`task-2-fix1-build.log`).
- `git diff --check` — clean.

Scope: this finding only; no tickets 03/04, unrelated test slices, push, merge or branch switch. Scoped rereview and the previously documented native Windows checks remain pending. The fix commit is the commit containing this appended report section and the tracked fix1 evidence; resolve it with `git log -1 --format=%H -- .superpowers/sdd/agent-diagnostics/task-2-report.md`.
