# Agent diagnostics V1

The Windows tray app automatically starts an authenticated, read-only Kestrel
service at an assigned `http://127.0.0.1:<port>`. `AFKtor.Core` owns the immutable
model and collector; `AFKtor.Diagnostics` owns HTTP and discovery. Linux tests
start that same host without WinForms or League. There are no action, settings,
League proxy, remote listener, or CORS endpoints.

Framework-dependent deployment now requires **both .NET 10 Desktop Runtime and
ASP.NET Core 10 Runtime** on Windows. Installing only Desktop Runtime is not
sufficient. A self-contained Windows publish bundles the required frameworks.
No HTTP.sys URL reservation or administrator setup is needed. The Core project
continues to reference only the base .NET runtime.

A cloud/Linux agent cannot automatically reach a live Windows localhost
listener. This feature does not create a tunnel or upload evidence. Run queries
on the Windows machine; provide retained files through an existing file-access workflow when offline.

## Discovery and queries

Descriptors are per instance under
`%LOCALAPPDATA%/AFKtor/diagnostics/discovery/<runId-without-dashes>/instance.json`.
The instance directory grants access only to the current Windows SID; on Unix it
has mode 0700 and the descriptor has mode 0600. The descriptor is atomically
published after binding and contains `schemaVersion`, `runId`, `build`, `endpoint`
and `token`. The token is fresh 32-byte cryptographic randomness, encoded as 64
hex characters. It appears only in the restricted descriptor and authentication
handling. Descriptors are removed on normal shutdown. After a crash, discard
unreachable descriptors and reject a response whose run identity differs.

PowerShell, on the Windows machine:

```powershell
$files = Get-ChildItem "$env:LOCALAPPDATA/AFKtor/diagnostics/discovery/*/instance.json"
$d = Get-Content $files[0].FullName -Raw | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $($d.token)"; 'X-AFKtor-Run-Id' = $d.runId }
Invoke-RestMethod "$($d.endpoint)/diagnostics/state" -Headers $headers
Invoke-RestMethod "$($d.endpoint)/diagnostics/runs" -Headers $headers
Invoke-RestMethod "$($d.endpoint)/diagnostics/events?runId=$($d.runId)&afterSequence=0&limit=50" -Headers $headers
```

For Linux integration tools, inject a discovery directory into
`DiagnosticHost.StartAsync(collector, directory)`. Read the JSON with your HTTP
client and set the same headers; do not print the descriptor/token into logs.
`X-AFKtor-Run-Id` is optional, but agents should always send it. A mismatch returns
409. Every successful response identifies schema 1; clients must reject an
unsupported schema. Do not depend on JSON property order.

## Versioned wire contract

All names are camelCase; enums are camelCase strings. UTC timestamps are ISO
8601, elapsed times are milliseconds from the process's monotonic clock.
`build` is the Core assembly informational version, including its commit ID when
provided by the SDK (at most 128 encoded UTF-8 bytes; otherwise `unavailable`). Events carry the same identity as state and run metadata.

`GET /diagnostics/state` returns this structural fixture (example values):

```json
{
  "schemaVersion": 1,
  "runId": "00000000-0000-0000-0000-000000000001",
  "build": "1.0.0+commit",
  "startedAt": "2026-11-07T10:00:00+00:00",
  "snapshotAt": "2026-11-07T10:00:01+00:00",
  "sequenceWatermark": 2,
  "connection": {
    "stage": "connected", "generation": 1, "reason": "none",
    "observedAt": "2026-11-07T10:00:01+00:00",
    "lastSuccessfulObservation": "2026-11-07T10:00:01+00:00"
  },
  "readyCheck": { "availability": "unavailable" },
  "arming": { "availability": "unavailable" },
  "settings": { "availability": "unavailable" },
  "operation": { "availability": "unavailable" },
  "health": {
    "api": "available", "discovery": "available", "storage": "unavailable",
    "queueDepth": 0, "producerDropped": 0, "sinkDropped": 0,
    "evicted": 0, "truncated": 0, "failureCategory": null
  }
}
```

The connection and sequence watermark are captured under one collector lock.
`observedAt` is the last captured transition time. Equal consecutive connection
observations are coalesced. `lastSuccessfulObservation` records the most recent
captured connected transition, not a continuously refreshed heartbeat. Unknown
fields start with null observation times and `unknown` stage. Groups are explicitly unavailable until their source is attached and observed.
See the ready-check contract below for the controller groups.

`GET /diagnostics/runs` returns `{ "schemaVersion": 1, "runs": [...] }`.
Each entry has `schemaVersion`, `runId`, `build`, `startedAt`, `isCurrent`, and
`persistence`. The current run reports storage availability; prior runs report
`persistedPrefixOnly`. Retained tombstones can identify runs whose events were
evicted. Only `/state` describes the current live run.

`GET /diagnostics/events?runId=<guid>&afterSequence=0&limit=50` defaults to the
current run, cursor zero, and limit 50. It returns:

```json
{
  "schemaVersion": 1,
  "runId": "00000000-0000-0000-0000-000000000001",
  "events": [{
    "schemaVersion": 1,
    "runId": "00000000-0000-0000-0000-000000000001",
    "build": "1.0.0+commit", "sequence": 1,
    "timestamp": "2026-11-07T10:00:00+00:00", "elapsedMilliseconds": 0.1,
    "code": "app.started", "severity": "information", "source": "app",
    "details": { "stage": "unknown", "generation": 0, "reason": "none" },
    "truncated": false
  }],
  "nextCursor": 1, "firstAvailableSequence": 1, "lastAvailableSequence": 2,
  "sequenceWatermark": 2, "hasMore": true, "historyEvicted": false,
  "droppedEvents": 0, "truncated": false, "completeness": "currentRunMemoryOnly"
}
```

Sequences strictly increase within a run for captured events. Wall-clock ties
or corrections never determine order. Feed `nextCursor` back as
`afterSequence`. Bounds describe the union of retained memory and disk at query time. An empty tail
has no events and `hasMore: false`; `historyEvicted: true` means the requested
cursor precedes the retained prefix. `droppedEvents` counts producer attempts
lost before a sequence could be assigned; their precise sequence positions are
unknown. Any such loss conservatively makes all pages `incomplete`, even when
the requested tail is empty. Sink losses affect persistence health, not the
available in-memory sequence. Pagination itself is indicated by `hasMore`;
`truncated` means detail/response truncation, never silent omission. Oversized safe records are explicitly marked `truncated`; page truncation is
true if a returned event was truncated.

Stable event codes: `app.started`, `app.stopped`, `connection.changed`.
Connection stages: `unknown`, `disconnected`, `discovering`, `connecting`,
`connected`. Reasons: `none`, `discoveryUnavailable`, `discoveryFailed`,
`sessionFailure`, `replaced`, `stopped`. Some values reserve future source detail.
App events have source `app`; connection events have source `session`.
No unchecked text, exceptions, raw bodies, lockfile data, or credentials enter
the typed producer. Error severity/type/stack details are unavailable in this
slice; Ready-check source details are described below; other source details follow in later tickets.

Errors contain only `{ "schemaVersion": 1, "error": "<fixedCode>" }`:
401 `unauthorized`; 405 `methodNotAllowed`; 409 `runMismatch`;
400 `invalidQuery` or `invalidRouteOrQuery`; 404 `runUnavailable` or
`invalidRouteOrQuery`; 429 `busy`; 503 `unavailable`, `storageUnavailable`, or `responseTooLarge`. An unknown run
returns `storageUnavailable` when storage cannot establish whether it exists.
Unknown query keys, duplicate values, malformed/negative cursors, invalid GUIDs,
and limits outside 1–100 are rejected. A request exceeding its deadline is
aborted. All responses disable caching. No credential/request/exception text is
reflected in errors. An oversized response fails explicitly rather than
returning a misleading partial page.

## Bounds and failure behavior

| Resource | Hard bound |
| --- | --- |
| In-memory events | 1,024, oldest first eviction |
| Producer work | One nonblocking lock attempt, one typed observation, O(1) queue/ring work; no formatting, callbacks, disk or HTTP |
| Event/detail | Fixed enum/numeric schema; at most 2,048 encoded bytes including a bounded build identity; no arbitrary detail strings |
| Sink queue | 256 events, failed enqueue increments sinkDropped |
| Sink call | 500 ms; one hung/throwing sink is disabled, queued loss counted, no automatic retries |
| Pagination | 1–100 events, default 50 |
| HTTP response | 262,144 encoded bytes; oversized responses return a small error |
| HTTP requests | 8 executing readers; excess requests get 429; a hung reader retains its slot |
| Transport | 32 connections, 8,192 header bytes, 2,048 request-line bytes, zero request body bytes |
| Deadlines | 2 seconds for startup, request handling, headers, and keepalive |
| Shutdown | Shared 2 seconds across collector, storage and host, then abandon remaining diagnostic work |
| Error metadata | Fixed codes, under 128 encoded bytes; no exception message/stack/path |

Health uses fixed availability/category strings and independent counters, without
recursively logging health events. A contended producer loses evidence rather
than waiting; its last captured state remains visible with its observation time
and loss count. Missing discovery access degrades discovery while retaining the
collector and bound host. A failing sink cannot call into application producers.
Hung sink/history tasks execute on background workers, never on the UI or
scheduling lock. No retry storms or unbounded work queues are created.

## Extension seams and validation

`DiagnosticCollector.Publish(DiagnosticObservation)` and `ConnectionChanged`
are the sealed producer interface. Extend the allowlisted observation/detail
records and coherent projection for further evidence; preserve their finite
encoded size and do not add caller-supplied formatting/callbacks.
`IDiagnosticSink.WriteAsync` receives immutable records only on the bounded
background worker. `IDiagnosticHistory.GetRuns/GetEvents` has cancellation-aware overloads; the host
passes its request deadline and isolates throwing/hung readers.
`DiagnosticCollector.GetMemoryHistory()` returns one coherent copy of the entire
1,024-event ring for retention-aware merging. `DiagnosticRuntime.Start` composes
the collector, default store, merger and host; optional sink/history overrides
remain available, with an optional `historyDirectory` for storage placement.
The default injected-directory layout is `<discoveryDirectory>/history`; the
Windows tray explicitly uses the separate standard history path below.

`DiagnosticHistoryTests`, `DiagnosticsTests`, `DiagnosticIsolationTests`, and the source-level session test
exercise real Linux HTTP, auth/discovery/mode checks, ordering/gaps, input/bounds,
secret exclusion, source transitions, blocked readers/sinks, and lifecycle.
Native Windows startup, inherited file ACL behavior, installed-runtime packaging,
and live League transitions remain manual checks. An independent review is
required before this ticket is considered integrated.


## Retained history and offline recovery

The tray stores JSON Lines under `%LOCALAPPDATA%/AFKtor/diagnostics/history`.
Discovery and its token remain separate. The first queued record initializes
storage on a background worker; constructing/starting the runtime performs no
storage ownership, scan or cleanup on the tray thread. Each complete newline
terminated event is flushed before being indexed as persisted. Only successfully
persisted records are promised across process exit; hardware/filesystem failure
can still lose evidence. The store never synthesizes `app.stopped` or a known
acceptance outcome after a crash.

| Storage resource | Bound/policy |
| --- | --- |
| Total history | 20,000,000 actual encoded bytes across all history files, including active segments, manifests, lock file and damaged leftovers |
| Lifetime | Seven days from each event's UTC timestamp; no expired event is returned |
| Segment | New segments rotate at 65,536 encoded bytes |
| Index | 512 segments, 32,768 events; these additional limits can evict evidence before the byte limit |
| Run metadata | 128 retained runs/tombstones, oldest removed first |
| Manifest | 262,144 bytes each for metadata.json and metadata.tmp; cleanup reserves both replacement files before writes |
| Directory scan | 1,024 entries; excess entries degrade persistence instead of allocating an unbounded index |
| Cleanup | On initialization, before writes/history reads, and every minute while running |
| Retry | One serialized storage attempt after 1, 2, 4, 8, 16, 32, then at most every 60 seconds; intervening writes are dropped/countable |

The byte budget includes every regular file in the dedicated history directory.
Do not place unrelated files there: unknown/damaged leftovers may be deleted to
restore the budget. Space reserved for replacement metadata means usable event
history can be smaller than 20 MB. Expired mixed-age segments are removed whole;
newer records lost with them are reported as retention gaps. Offline aging is
cleaned at the next initialization. A failed cleanup stops writes rather than
allowing history to grow; age-filtered indexed records remain readable during
backoff. Cleanup and synchronous filesystem operations run only on diagnostic
workers. A filesystem operation that ignores cancellation can be abandoned at
the shared shutdown deadline; its background task may finish later.

Events are merged by `(runId, sequence)` with memory winning duplicate keys,
then sorted by sequence. Expiry is applied before memory pagination, so an old
memory prefix cannot hide newer evidence. Event pages add `gaps`, `storage`, and
`persistedThroughSequence`. The last field is the greatest currently retained
persisted sequence, **not** proof that every preceding record or the crash tail
survived. Gaps use fixed categories: `expired`, `evicted`, `corruption`,
`unpersisted`, `sequenceGap`, `producerDropped`, and `storageUnavailable`.
Collector queue loss adds `unpersisted` even when the store itself is healthy.
A disabled collector sink keeps current-run persistence unavailable for the rest
of that run; independently recovered storage reads do not resume writes. Known
gaps in the bounded disk/memory union are reported on every page, including an
empty tail and an intact early page. Store-wide gap metadata is deliberately conservative and may affect unrelated
retained runs; exact unknown loss positions are never invented. Global producer
loss has no assigned sequence. Storage loss is counted separately in
`health.persistence.dropped` and collector queue loss in `health.sinkDropped`.

Prior-run pages are `persistedPrefixOnly`, or `incomplete` when established gaps
exist. Current pages are `currentRunMemoryAndDisk`, or `incomplete`. A retained
run with no remaining events can return an empty page with loss reasons; an
unknown run returns 404 when storage is available and 503 `storageUnavailable`
when storage cannot establish its existence. Run tombstones are bounded, so
removed older identities cannot always be distinguished from never-seen runs.
Health includes `persistence` with `availability`, `failureCategory`, `dropped`,
`truncated`, `deletedSegments`, and `gaps`. Storage categories are
`ownershipUnavailable` and `storageUnavailable`, without paths or exception text.
The API state reads the current store health even when cleanup occurs while idle.

One exclusive `owner.lock` file handle owns a history directory. A second
instance retains live memory/API/discovery but degrades disk persistence until
ownership can be retried. The OS releases ownership on process exit; the mere
presence of the lock file does not block recovery. Discovery identities stay
per instance. Normal shutdown drains queued records when possible, then closes
storage under the same two-second diagnostic budget.

For offline inspection, copy the history directory after stopping the app and
read complete JSONL lines ordered by run ID and sequence, deduplicating keys.
Treat malformed lines and incomplete final lines as lost evidence. For API
inspection after restart, query `/diagnostics/runs`, then request the desired
prior run explicitly. Keep the original damaged directory when troubleshooting;
work on a copy if you need to preserve evidence before retention cleanup.
For unavailable storage, check free space, directory write/delete permission,
other live AFKtor instances and leftover file locks, then allow the bounded retry.
Never infer a clean shutdown or an accepted match from a missing event.

Native Windows checks remain: NTFS sharing/locking and crash release, deletion
and replacement while files are open, inherited history-directory ACLs, denied
access/full-volume behavior, and startup/shutdown on a slow filesystem. Linux
permission tests and a Windows Release cross-build do not establish these native
filesystem guarantees.

## Ready-check decision evidence (ticket 03)

The tray wires its real `ReadyCheckController` to the same collector as its
session monitor. No display-string polling or separate decision engine is used.
With no controller attached the four source groups remain `unavailable`; once
observed, `availability: available` means instrumentation is available, not that
a ready check exists. Settings load/save failures and startup/LCU transport stages
outside the accept request remain ticket 04 scope.

`readyCheck` contains `generation`, nullable local `checkId`, `baseline`,
allowlisted `state`, `response`, nullable server `timer`, and `observedAt`.
Unknown/unobserved values use `unknown` and null; disconnect/session replacement
clears the observation. IDs are monotonically assigned locally within the run;
they are not League IDs. An answered in-progress check remains the same observed
lifecycle. `arming` contains `armed`; `settings` contains effective `delaySeconds`.
These settings are controller-effective values, not evidence of a successful save.

`operation` contains its own `generation`, `checkId`, nullable `attemptId`, `stage`
(`none`, `waiting`, `verifying`, `dispatched`, `finished`), captured delay in
seconds, `deadline`, nullable `remainingSeconds`, and `outcome`. A finished
operation can refer to the preceding check, so always use its own IDs. Remaining
time exists only while waiting and is decreased by the collector's monotonic
clock between observations. Source clocks are UTC-anchored monotonic clocks;
wall-clock corrections do not move countdown deadlines. Injected test clocks
can use another UTC anchor. Source `observedAt` is distinct from collector event
capture `timestamp`; neither wall-clock value establishes event ordering.

Controller records have source `controller` and `details.controller`. This fixed
shape includes a source `revision`, decision `observedAt`, the four coherent
source groups above, and **decision-time** `eventGeneration`, `eventCheckId`,
`eventAttemptId`, `eventStage`, and `eventOutcome`. Nullable optional detail fields
are omitted in JSON; absent values mean unavailable, not zero. Stale updates
retain their source generation and have no invented check/attempt ID. Late
verification/completion records retain the original attempt IDs while their
four source groups describe the current controller projection. Use the event
identity fields to reconstruct an attempt; use `/state` to inspect current work.

Source capture and sealed nonblocking ingress occur under the controller's
scheduling lock. Only fixed in-memory work runs there: no formatting, injected
sink, disk, HTTP server, or user diagnostic callback. The collector atomically
captures the event sequence and source projection. It accepts only newer source
revisions for live state, so late evidence cannot regress it. A dropped producer
attempt leaves the last captured projection visible with its observation time;
health loss and page completeness remain authoritative. Revision increases can
also reflect state-only duplicate observations and are not a count of events.

Stable controller codes and how to interpret them:

| Codes | Evidence |
| --- | --- |
| `arming.changed`, `delay.changed` | `previousArmed` / `previousDelaySeconds` and new effective source group; an active operation keeps its captured delay and deadline |
| `readyCheck.sessionStarted`, `readyCheck.baseline` | Connection generation and initial-baseline establishment |
| `readyCheck.detected`, `readyCheck.initialSkipped`, `readyCheck.disarmedSkipped` | Local check identity and each applicable initial/disarmed skip reason |
| `readyCheck.observed`, `readyCheck.timerBackwards`, `readyCheck.staleUpdate` | Safe observed fields, conservative backwards-timer suppression, or rejected source generation |
| `readyCheck.duplicates` | Count and first/last times of repeated identical current updates |
| `accept.scheduled`, `accept.verifying` | Attempt identity, captured delay/deadline, and operation stage |
| `accept.verification` | `initialTimer`, `lastTimer`, `returnedTimer`, `localElapsedSeconds`, `requiredTimer`, and every failed condition |
| `accept.cancelled` | Fixed cancellation cause and stage/outcome at cancellation |
| `accept.dispatched`, `accept.completed`, `accept.failed`, `accept.finished` | Dispatch boundary, HTTP result/status when known, latency, failure category, and uncertainty |
| `readyCheck.disconnected` | Observation invalidation; arming persists |

Identical current updates do not create one event per update. The live group
tracks `duplicateCount`, `firstDuplicateAt`, and `lastDuplicateAt`; a single
summary event is flushed before the next changed update, session replacement,
or disconnect. An abrupt crash can lose an unfinished summary; current live
state still exposes it while running. The count excludes the first observation.
Stale updates are separate rejected decisions and never replace current fields.
The existing ring/queue limits, drop/gap metadata and generic store apply to these
records unchanged. Optional nulls and inapplicable default reason fields are
omitted. Build identities are limited to 128 **encoded UTF-8 bytes** at collector
construction (otherwise `unavailable`), keeping the fixed record shape within the
2,048-byte bound without formatting in producer calls.

To explain an accept or skip, filter events by run and event generation/check/
attempt IDs, then follow sequence order. Start at baseline/detection, check arming,
inspect the scheduled deadline, and inspect `accept.verification`. The required
timer is the initial server timer plus configured countdown plus any extra local
wait before verification starts. `localElapsedSeconds` is actual elapsed time at
the decision, including final-read latency; the threshold remains the one used
by the existing controller when verification began. Failure flags can combine
`notPending`, `timerBackwards`, `insufficientTimer`, and `staleOperation` (JSON
flags are comma-separated camelCase names); no failures is `none` or omitted.
A failed verification never dispatches. Zero delay still requires a noninitial,
armed pending check and a compatible final timer. A backwards update cancels
conservatively instead of treating a timer reset as a new check.

Cancellation causes are `disarmed`, `sessionReplaced`, `checkEnded`,
`timerBackwards`, `disconnected`, or `disposed`. A waiting/verifying cancellation
is before dispatch. A dispatched cancellation has `eventOutcome: unknown`:
the request cannot be recalled. Success is `completedUnconfirmed`, which means
only HTTP completion, never confirmation that League accepted the match.
Post-dispatch failures/timeouts remain `unknown` and never cause an automatic
retry; pre-dispatch failures are `failed`. `failure` is an allowlisted category
(`http`, `io`, `invalidData`, `timeout`, `cancelled`). `httpStatus` is present only
for known HTTP statuses; simulated clients can leave it unavailable. Latency is
in milliseconds from dispatch, using the source monotonic clock. No response
body, credentials, unchecked exception text, or token enters this evidence.

Linux coverage composes the actual controller, collector and authenticated
loopback host with simulated League behavior and a controllable clock. It also
roundtrips controller records through generic history recovery and exercises
throwing/hung/saturated/contended diagnostics. This establishes neither native
Windows tray behavior nor installed League behavior; those remain manual checks.
