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
on the Windows machine; sharing retained files is a later workflow.

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
provided by the SDK (at most 128 characters; otherwise `unavailable`). Events carry the same identity as state and run metadata.

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
fields start with null observation times and `unknown` stage. Explicitly
unavailable groups are reserved for subsequent source instrumentation.

`GET /diagnostics/runs` returns `{ "schemaVersion": 1, "runs": [...] }`.
Each entry has `schemaVersion`, `runId`, `build`, `startedAt`, `isCurrent`, and
`persistence`. This slice reports only the current run with `memoryOnly`; it
makes no persistence or crash-completeness promise.

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
`afterSequence`. Bounds describe retained memory at query time. An empty tail
has no events and `hasMore: false`; `historyEvicted: true` means the requested
cursor precedes the retained prefix. `droppedEvents` counts producer attempts
lost before a sequence could be assigned; their precise sequence positions are
unknown. Any such loss conservatively makes all pages `incomplete`, even when
the requested tail is empty. Sink losses affect persistence health, not the
available in-memory sequence. Pagination itself is indicated by `hasMore`;
`truncated` means detail/response truncation, never silent omission. Current
typed details fit the fixed event budget, so truncation is false in this slice.

Stable event codes: `app.started`, `app.stopped`, `connection.changed`.
Connection stages: `unknown`, `disconnected`, `discovering`, `connecting`,
`connected`. Reasons: `none`, `discoveryUnavailable`, `discoveryFailed`,
`sessionFailure`, `replaced`, `stopped`. Some values reserve future source detail.
App events have source `app`; connection events have source `session`.
No unchecked text, exceptions, raw bodies, lockfile data, or credentials enter
the typed producer. Error severity/type/stack details are unavailable in this
slice; source-specific safe details follow in later tickets.

Errors contain only `{ "schemaVersion": 1, "error": "<fixedCode>" }`:
401 `unauthorized`; 405 `methodNotAllowed`; 409 `runMismatch`;
400 `invalidQuery` or `invalidRouteOrQuery`; 404 `runUnavailable` or
`invalidRouteOrQuery`; 429 `busy`; 503 `unavailable` or `responseTooLarge`.
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
| Shutdown | Shared 2 seconds across collector and host, then abandon remaining diagnostic work |
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
background worker. `IDiagnosticHistory.GetRuns/GetEvents` lets the next slice
merge persisted and current events behind the same HTTP contract. It must enforce
retention, bound results and explicitly account for storage gaps. The host
isolates throwing/hung history readers. `DiagnosticRuntime` composes the default
collector/host and accepts optional sink/history implementations. Storage is not
implemented here; seven-day/20,000,000-byte retention belongs to ticket 02.

`DiagnosticsTests`, `DiagnosticIsolationTests`, and the source-level session test
exercise real Linux HTTP, auth/discovery/mode checks, ordering/gaps, input/bounds,
secret exclusion, source transitions, blocked readers/sinks, and lifecycle.
Native Windows startup, inherited file ACL behavior, installed-runtime packaging,
and live League transitions remain manual checks. An independent review is
required before this ticket is considered integrated.
