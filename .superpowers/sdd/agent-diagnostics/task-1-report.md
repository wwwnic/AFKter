# Ticket 01 implementation report

Status: DONE_WITH_CONCERNS — implementation and automated validation complete; independent review and native checks pending.

## Scope and architecture

Implemented ticket 01 only on `codex/agent-diagnostics`, based on `6006378`.
Core owns immutable typed diagnostic contracts and a sealed, bounded, nonblocking
collector. No ASP.NET/WinForms dependency was added to Core. The new
`AFKtor.Diagnostics` .NET 10 project references Core and the ASP.NET Core shared
framework. It owns the real Kestrel listener, secure per-instance discovery,
request isolation, and automatic application lifetime composition. The tray starts
it asynchronously and stops it under a shared two-second diagnostics budget.
ReadyCheckSessionMonitor publishes connection transitions at their source, outside
controller scheduling locks. No display-string sampling was added.

App startup/shutdown and connection transitions are captured. State marks arming,
settings, ready-check, and operation groups explicitly unavailable. Current-run
memory is the only history in this slice; storage is explicitly unavailable unless
a background sink is supplied. No false durable-history guarantee is made.

## Public interfaces for tickets 02–04

- Namespace `AFKtor.Core.Diagnostics`:
  - `DiagnosticCollector(IDiagnosticSink? sink = null)`; `RunId`, `StartedAt`, `Build`.
  - `Publish(DiagnosticObservation)` and `ConnectionChanged(ConnectionStage, long generation, DiagnosticReason)` are the sealed producer seam; no injected producer callback/interface can hang app code.
  - `GetState()`, `GetRuns()`, `GetEvents(Guid runId, long afterSequence, int limit)` are coherent read projections.
  - `SetApiHealth(bool)`, `SetDiscoveryHealth(bool)` use fixed safe categories.
  - `IDiagnosticSink.WriteAsync(DiagnosticEvent, CancellationToken)` executes only on a background worker; bounded queue, 500 ms timeout, disable on first failure, account queued loss. A later persistence implementation must honor cancellation and own its flush/resource lifetime as needed.
  - `IDiagnosticHistory.GetRuns()` and `GetEvents(...)` are the HTTP read seam. Ticket 02 should implement a bounded retention-aware merger with live collector data. Unknown run returns null. Enforce completeness/storage-gap semantics explicitly rather than pretending prior runs have current snapshots.
  - `DiagnosticObservation` currently contains enum code/stage/reason and numeric generation only; extend allowlisted typed fields for later tickets, preserving bounded encoded size, nonblocking ingress and coherent state projection.
  - `DiagnosticEvent`, `DiagnosticState`, `ConnectionDiagnosticState`, `DiagnosticRun`, `DiagnosticEventPage`, `DiagnosticHealth`, and `DiagnosticLimits` are the immutable wire/read records and constants.
- Namespace `AFKtor.Diagnostics`:
  - `DiagnosticHost.StartAsync(DiagnosticCollector, string discoveryDirectory, IDiagnosticHistory? history = null, CancellationToken = default)` runs the real loopback server. Endpoint and discovery path are nullable on degraded startup/access. Disposal is bounded.
  - `DiagnosticRuntime.Start(string directory, IDiagnosticSink? sink = null, IDiagnosticHistory? history = null)` starts without waiting; `Collector` and `Started` expose the composed services. Runtime disposal shares a two-second budget across collector and host.
  - `DiscoveryDescriptor` and `DiagnosticJson.Options` define discovery and camelCase/string-enum serialization.
- Session monitor's final optional constructor parameter is `DiagnosticCollector? diagnostics = null`; existing callers remain compatible.
- Full HTTP fields, structural JSON fixtures, codes, cursor/loss semantics and PowerShell discovery/query examples: `docs/agent-diagnostics.md`.

## Finite limits and observable loss

- 1,024 retained in-memory events; oldest eviction counted; current bounded history is always available independently of sink status.
- Producer: one `Monitor.TryEnter`, O(1) event/ring/queue work, no formatting/I/O/user callbacks; contended calls count as producer loss before sequence assignment. Duplicate unchanged connection tuples are coalesced.
- Fixed allowlisted enum/numeric details and build identity capped at 128 characters; event bound 2,048 bytes; no arbitrary detail/exception strings. Oversized build metadata becomes `unavailable`.
- 256 sink queue entries; failed enqueue increments sinkDropped. Sink calls get 500 ms, then the sink is disabled, its queued loss counted, and at most one hung call remains. No automatic retries.
- 1–100 events per page; default 50. Response maximum 262,144 encoded bytes. Oversized response returns fixed `responseTooLarge`, never a misleading partial page.
- 8 concurrent readers, 32 transport connections, 8,192 header bytes, 2,048 request-line bytes, zero body bytes.
- Two-second request/startup/header/keepalive deadlines. Hung history readers keep their slot, so repeated deadline failures cannot create unbounded abandoned readers.
- Shared two-second diagnostic shutdown budget; hung work is on background pool threads.
- Error metadata uses only fixed codes under 128 bytes. No unchecked exception text or absolute machine paths.
- `historyEvicted`, available sequence bounds, global `droppedEvents`, `hasMore`, `truncated`, and `completeness` distinguish evicted/lost history from an empty tail. Producer losses are not assigned fabricated sequence positions. Sink loss is separate from still-queryable memory.

## TDD and verification evidence

Tool environment for all dotnet commands:
`DOTNET_CLI_HOME=/workspace/.dotnet-home NUGET_PACKAGES=/workspace/.nuget/packages MSBuildEnableWorkloadResolver=false /workspace/.dotnet/dotnet`.

- Parent baseline: 95 tests passed before implementation (`baseline.log`).
- Initial test-first compile RED: missing diagnostics namespaces/contracts (`task-1-red.log`). An initial test constraint typo was corrected before runtime RED.
- Runtime RED: four real-listener tests failed with NotImplementedException against the host skeleton (`task-1-red-runtime.log`).
- Source instrumentation RED: the session test observed Unknown instead of Connected before source publishing was added (`task-1-green-initial.log`; 4 HTTP tests already passed).
- Runtime composition RED: lifecycle test failed against runtime skeleton; response-bound/deadline test passed (`task-1-runtime-red.log`).
- Explicit unavailable-group RED: missing arming/settings/operation properties caused the state contract test to fail (`task-1-unavailable-red.log`).
- Focused GREEN: 10 tests passed before the final cancelled-startup/unavailable-group additions (`task-1-focused.log`). Isolation/bounds assertions also verify synchronous hung and throwing sinks, saturation, invalid typed values, parallel producer loss, ordered pagination/eviction, secret exclusion, real auth/discovery/Unix modes, stale identity, unsupported methods/invalid input/no CORS, and safe oversized errors and reader deadlines.
- Final full suite command: `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true` — **106 passed, 0 failed, 0 skipped** (`task-1-full-test.log`).
- Windows Release command: `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true` — **succeeded, 0 warnings, 0 errors** (`task-1-build.log`).
- `git diff --check` — clean.

## Files

New: `src/AFKtor.Core/Diagnostics/DiagnosticContract.cs`, `DiagnosticCollector.cs`;
`src/AFKtor.Diagnostics/AFKtor.Diagnostics.csproj`, `DiagnosticHost.cs`,
`DiagnosticRuntime.cs`, `DiagnosticJson.cs`, `DiscoveryFile.cs`;
`tests/AFKtor.Tests/DiagnosticsTests.cs`, `DiagnosticIsolationTests.cs`;
`docs/agent-diagnostics.md`.
Modified: solution and tray/test project references; session monitor and its tests;
tray application composition; README; ticket 01 dated implementation comment.

## Concerns and follow-up

- Independent ticket review is intentionally pending; root dispatches the fresh reviewer.
- Native Windows startup, per-user ACL behavior, installed-runtime packaging and live League transitions cannot be exercised in this Linux environment. Windows Release cross-compilation is clean. Framework-dependent deployment now needs ASP.NET Core 10 Runtime in addition to Desktop Runtime.
- This ticket does not implement prior-run persistence, full source failure detail or ready-check decision evidence. Tickets 02–04 should extend the documented contracts rather than misrepresent these as available now.
- Failures disable a supplied sink for the rest of that run. Recovery/retry policy can be deliberately extended with bounded backoff in the storage ticket; no unbounded retry loop exists now.
- `lastSuccessfulObservation` means last captured transition to connected, not a sampled heartbeat. State remains timestamped and loss-aware after dropped observations.

## Commits

`422e252` — Add bounded live agent diagnostics API (18 scoped source/test/docs/ticket files).
No push, merge, branch switch or subagent execution was performed.

## Review fix round 1 — deterministic producer contention

The reviewer identified a test scheduling assumption: `Parallel.For` need not
produce any collector contention on a runtime exposing one processor. Reproduced
before editing with:

```sh
DOTNET_PROCESSOR_COUNT=1 DOTNET_CLI_HOME=/workspace/.dotnet-home NUGET_PACKAGES=/workspace/.nuget/packages MSBuildEnableWorkloadResolver=false /workspace/.dotnet/dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj --no-build --filter FullyQualifiedName~InvalidEnumsCannotSmuggleTextAndParallelProducersReportLoss
```

RED result: **1 failed**, expected ProducerDropped greater than 1, actual 1
(`task-1-fix1-red.log`). Production was behaving correctly under serialized work.

Changed only `tests/AFKtor.Tests/DiagnosticIsolationTests.cs`: invalid enum
accounting is now its own test. The contention test reflects the existing private
collector gate, holds it on the test thread, and invokes the producer on a
dedicated background thread. It requires that producer to return while the gate
is still held, then checks exactly one loss, an unchanged watermark/projection,
incomplete history, and successful publishing after the gate is released. It
uses bounded waits and releases the gate before checking assertions. No public
production test hook or production behavior was added.

Focused GREEN commands (same DOTNET_CLI_HOME/NUGET_PACKAGES/MSBuild environment
as above):

```sh
/workspace/.dotnet/dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj --filter FullyQualifiedName~DiagnosticIsolationTests
DOTNET_PROCESSOR_COUNT=1 /workspace/.dotnet/dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj --no-build --filter FullyQualifiedName~DiagnosticIsolationTests
```

Results: **4 passed, 0 failed** under the default processor count
(`task-1-fix1-default.log`), and **4 passed, 0 failed** with one processor
(`task-1-fix1-single-cpu.log`). `git diff --check` is clean. No full suite or
Windows rebuild was repeated: this is the explicitly scoped test-only review
fix, with production unchanged. Scoped rereview remains pending.

Fix commit: `d0e448a` — Make diagnostic contention test deterministic.
