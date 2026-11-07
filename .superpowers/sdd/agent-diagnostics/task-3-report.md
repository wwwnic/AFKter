# Ticket 03 delivery report

Date: 2026-11-07. Branch: `codex/agent-diagnostics`. Starting checkpoint: `4b4b275`.
Implementation is complete and committed for independent review; no push or merge
was performed by the implementer. Ticket 04 and the deferred ticket 02 unknown-run
503/404 classification are unchanged. Independent review is owned by the parent
agent and is not claimed as complete here.

## Implementation

The real controller now publishes safe typed evidence directly into the sealed
collector, and the Windows tray supplies its runtime collector to that controller.
The existing read-only API automatically exposes coherent controller state and
correlated current-run evidence. Generic history serialization/recovery already
supports the optional typed nested details, so persistence needed no source-
specific branch, schema interpretation or code change.

Evidence includes effective arming and delay edits (old/new values), baseline and
check detection, initial and disarmed skips, observations, rejected stale updates,
bounded duplicate aggregation, scheduling, every actual cancellation cause,
verification inputs/all failed predicates, dispatch, HTTP result/status, request
latency, safe failure categories, completion and uncertainty. Check IDs and attempt
IDs are local run-scoped increasing counters, not server identifiers. Operation
identity survives replacement/disconnect and asynchronous completion.

A completed POST is `completedUnconfirmed`. Postdispatch failure, timeout or
cancellation is `unknown`; no automatic retry was introduced. Pre-dispatch
failures are `failed`. Cancellation records preserve the stage at the cancellation
boundary, so waiting/verifying can be distinguished from dispatched cancellation.
The existing conservative initial-pending, backwards-timer, zero-delay and final
server-timer rules remain enforced.

## Interfaces and exact bounds

- `ReadyCheckController(delay?, now?, DiagnosticCollector? diagnostics = null)`:
  existing two-argument callers remain valid. The optional dependency is the sealed
  collector, not an arbitrary injected diagnostic callback or implementation.
- `DiagnosticCollector.ObserveController(ControllerDiagnosticEvidence, bool
  stateOnly = false)` is **internal** to Core. External code cannot inject arbitrary
  strings into this source producer. Existing public enum-only ingress is retained.
- Immutable `ReadyCheckDiagnosticState`, `ArmingDiagnosticState`,
  `DelayDiagnosticState`, `OperationDiagnosticState` replace the four unavailable
  placeholders while preserving their availability marker. Without source evidence
  they still explicitly report `unavailable`.
- `DiagnosticDetails.Controller` is an optional typed nested record. Existing
  connection/app records and previously persisted records deserialize with null
  controller details. Optional null values and inapplicable default reason fields
  are omitted. Enums and flags serialize using existing camelCase rules.
- `IReadyCheckClient.AcceptWithStatusAsync` is a backward-compatible default
  interface method returning nullable status; it calls the existing `AcceptAsync`
  exactly once. Simulated/older clients return unavailable status. The real LCU
  implementation returns actual success status; HTTP exceptions contribute only
  allowlisted failure category and status in 100–599. No messages or bodies enter
  the producer.
- Source snapshot/event captures occur **under the controller scheduling gate**,
  rather than releasing it and later assembling unrelated live property reads.
  Work there is fixed memory/enum/scalar capture and a single nonblocking collector
  lock attempt. No formatting, serialization, disk, diagnostic HTTP work, sink or
  user diagnostic callback executes there. Existing League invocation remains at
  the original serialized dispatch boundary.
- Source revisions increase on every observation attempt, including state-only
  duplicate updates. Collector event sequence increases only on captured events.
  The newest accepted revision determines the live projection. Late asynchronous
  decisions compose the current projection but retain their original event
  generation/check/attempt/stage/outcome. A rejected stale update has unknown
  event check/attempt identity, never the current check ID. A cancelled operation's
  final read uses its frozen last timer, including when a newer check exists in
  the **same** session.
- Dropped ingress leaves the last captured projection and its source observation
  time, increments producer loss and makes event pages incomplete. No attempt is
  made to infer missing decisions. Memory ring remains 1,024, sink queue 256,
  nonblocking ingress O(1), sink timeout 500 ms, and shared diagnostic shutdown
  budget 2,000 ms. Existing 1–100 pagination, 262,144-byte responses, eight executing
  readers and all transport/history limits are unchanged.
- Event size remains 2,048 encoded bytes including newline in persistence. Build
  identity is checked once at construction against 128 encoded UTF-8 bytes,
  otherwise `unavailable`; no build formatting occurs in producer calls. Fixed
  typed numeric/enumerated fields and omitted nullable details keep these records
  within the existing bound. Tests check all emitted scenario records and a
  largest-server-timer fixture with a maximum-length build identity. Generic
  persistence retains its existing explicit truncation fallback for oversized
  externally supplied safe records, and existing gap/drop metadata remains intact.
- Duplicate current updates aggregate one count and first/last times in live
  state. One summary flushes before a changed update, session replacement or
  disconnect; unchanged updates do not enqueue one record each. A crash can lose
  an unfinished duplicate summary, as it can lose other unflushed evidence.
- Countdown clock remains monotonic, now anchored to UTC once for meaningful
  source observation/deadline timestamps. Injected manual-clock tests use the
  Unix epoch. Remaining duration is captured from the actual controller operation
  and decreases with collector monotonic elapsed time between source observations.
  Diagnostic availability is not a freshness guarantee.

## Focused RED/GREEN evidence

All commands source `/workspace/setup/activate.sh` and export
`MSBuildEnableWorkloadResolver=false`. Actual SDK: **10.0.403**, recorded in
`task-3-toolchain.log` (checkpoint used 10.0.400).

1. `task-3-red-1.log`: missing new API/state members caused compilation failures,
   demonstrating the first contract did not exist. `task-3-green-1.log`: the real
   controller/collector correlation/edit test passes (1/1).
2. `task-3-red-2.log`: 13 expected assertion failures and 1 pass, for missing
   baseline/skips/duplicates/cancellation/verification/outcome evidence.
   `task-3-green-2.log`: controller and diagnostic focused suite passes 51/51.
3. `task-3-red-3.log`: actual success HTTP status was unavailable (expected 204,
   null returned), while the other 19 scenarios passed. `task-3-green-3.log`:
   diagnostics/controller/transport suite passes 74/74, including success/failure
   status and generic restart persistence roundtrip.
4. `task-3-red-4.log`: missing remaining-duration API caused compilation failures.
   `task-3-red-4-bounds.log`: remaining-duration test passes, encoded-record stress
   test fails at 2,072 bytes with a 128-character JSON-escaped build. Optional
   null/default omission plus an actual **encoded-byte** build identity bound
   resolve this; the stress fixture now uses the maximum valid 128-byte build
   identity. `task-3-green-4.log`: 22/22 diagnostic tests pass.
5. `task-3-red-5.log`: two assertion failures exposed stale source check identity
   and a final read incorrectly comparing to a newer same-session check timer.
   `task-3-green-5.log`: 78/78 focused diagnostics/controller/transport tests pass.

New tests use the actual controller, collector and authenticated localhost Kestrel
API with no persistence required for the decision scenarios. A simulated HTTP
handler exercises the actual LCU implementation for 204 and 409 plus secret body/
credential markers. Generic history write/dispose/restart tests recover controller
records untruncated, including status. Isolated tests saturate/fail/hang the sink
and deliberately hold the collector lock on a different thread; controller
countdown, once-only dispatch and cancellation continue without waiting for
telemetry. Existing diagnostic shutdown isolation tests remain in the full suite.

## Final verification

- `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true`: **151 passed, 0 failed,
  0 skipped** (127 baseline plus 24 new cases). Full output:
  `task-3-final-test.log`.
- `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`:
  **succeeded, 0 warnings, 0 errors**, including `net10.0-windows` tray output.
  Full output: `task-3-final-build.log`.
- `git diff --check`: passed. No unrelated source/history/read-classification work.

Focused commands use `dotnet test tests/AFKtor.Tests/AFKtor.Tests.csproj
-p:EnableWindowsTargeting=true --filter ...`, with filters for
`ReadyCheckDiagnosticsTests`, `ReadyCheckControllerTests`,
`LcuReadyCheckClientTests` or named regression cases; their recorded logs show
actual counts and failures. RED compilation logs are explicitly distinguished
from failing executable assertions above.

## Changed files

- `src/AFKtor.Core/Diagnostics/DiagnosticContract.cs`: typed fixed-shape source
  state and event evidence, safe reason/outcome enums, optional controller details.
- `src/AFKtor.Core/Diagnostics/DiagnosticCollector.cs`: sealed source ingress,
  revision-aware atomic projection/event capture, current remaining duration and
  encoded-byte build bound.
- `src/AFKtor.Core/ReadyCheckController.cs`: source instrumentation, local IDs,
  duplicate aggregation, retained stage/outcome/last timer, monotonic UTC anchor.
- `src/AFKtor.Core/ReadyCheckSnapshot.cs`: optional status-bearing accept contract.
- `src/AFKtor.Core/LcuReadyCheckClient.cs`: safe numeric accept success status.
- `src/AFKtor.Tray/TrayApplicationContext.cs`: runtime collector wiring.
- `tests/AFKtor.Tests/ReadyCheckDiagnosticsTests.cs`: 24 new cases and loopback,
  simulated transport, persistence and isolation helpers.
- `docs/agent-diagnostics.md`: source wire contract and agent reconstruction guide.
- `.scratch/agent-diagnostics/issues/03-explain-ready-check-decisions-and-accept-attempts.html`:
  appended implementation comment; previous comments and status vocabulary retained.
- This report and ticket-specific log artifacts are force-added under ignored
  `.superpowers/sdd/agent-diagnostics`; prior delivery artifacts are preserved.

## Self-review and concerns

Reviewed the source diff for changed eligibility, dispatch/cancellation ordering,
source identity lifetimes, unchecked string escape routes, bounds, nullable/enum
serialization, backward interface compatibility and tray composition. Conservative
eligibility and minimum timer computation remain the existing algorithm. Source
instrumentation is bounded and cannot invoke an injected diagnostic implementation.
The source comparison regressions discovered during development have focused
failing-first tests and fixes. No known blocking implementation concern remains
from self-review; independent review is still required.

The retained ticket 02 unknown-prior-run classification Minor remains explicitly
unchanged. An HTTP completion cannot confirm League acceptance. An absent final
outcome after a crash or loss is unknown, not failure or success. Diagnostic
contention can intentionally discard observations; last observation time and
existing loss/completeness fields must be checked. Duplicate summaries can remain
live-only until their group ends. Remaining duration extrapolates the last
captured source observation and does not recover dropped controller state.

Linux controllable-clock, simulated transport, generic disk and loopback tests do
not verify native Windows startup/tray interactions, installed runtime packaging,
effective Windows discovery ACLs, NTFS ownership/crash-release/permissions,
denied/full/slow native filesystems or live League/WSS timing. Those native/live
checks remain unverified, as at the checkpoint. No claim of live acceptance is made.
