# Ticket 01 independent review

Reviewed base `6006378` through head `422e252b583450ccd774496c2d79b95527d3d79b`.

## Verdicts

**Spec Compliance: PASS for the live/current-run ticket scope, with native verification pending.** The source and supplied evidence cover the ticket's live API, source connection observations, coherent snapshot, bounded current-run history, discovery/authentication, lifecycle composition, fault isolation, and documentation. Durable retention and the fuller decision/evidence model belong to later tickets and are not claimed as delivered here.

**Task Quality: CHANGES REQUESTED.** One Important test portability/reliability issue was reproduced on the exact-head compiled assembly. No Critical issues or additional Important production-code issues were identified in this scoped review.

## Strengths

- Core retains an immutable typed model and sealed producer ingress with one nonblocking lock attempt. Connection instrumentation is at session transition sites, after controller calls return, rather than display-string sampling. No credential or unchecked exception text is accepted by that producer model.
- State and its sequence watermark share the collector lock. Pages distinguish evicted prefixes, lost producer observations, more results, and an empty tail. Unsupported state groups are explicitly unavailable.
- The actual Kestrel host binds explicitly to IPv4 loopback on an assigned port, uses fresh 32-byte token randomness and constant-time comparison, restricts requests to reads, and checks optional run identity. The descriptor is atomically published within a restricted per-instance directory after binding.
- Explicit history, queue, pagination, transport, reader, response, and lifetime bounds are documented alongside deployment implications. Hung reader work retains its admission slot, and sink failure disables further sink work without placing callbacks or I/O on the producer path.
- Tests exercise the real listener and source monitor, with useful RED evidence and clean full-suite/build results. Documentation explicitly identifies Windows runtime dependencies, pending native checks, and the lack of automatic cloud access to Windows loopback.

## Findings

### Important — Make the producer-contention test deterministic on single-processor hosts

**Location:** `tests/AFKtor.Tests/DiagnosticIsolationTests.cs:40` (setup at line 38).

**Trigger:** Run `InvalidEnumsCannotSmuggleTextAndParallelProducersReportLoss` on a runtime exposing one processor. `Parallel.For` is allowed to execute its iterations without overlapping collector critical sections. The initial invalid enum still increments `ProducerDropped` to 1, while every valid connection observation can correctly succeed.

**Effect:** The test requires `ProducerDropped > 1` and fails on a supported Linux execution configuration despite correct collector behavior. The test therefore does not reliably establish the intended contention contract and can fail on constrained CI runners.

**Focused reproduction:** Using the existing Debug assembly from the reviewed head, without rebuilding or rerunning the full suite:

```sh
env DOTNET_PROCESSOR_COUNT=1 DOTNET_CLI_HOME=/workspace/.dotnet-home NUGET_PACKAGES=/workspace/.nuget/packages MSBuildEnableWorkloadResolver=false /workspace/.dotnet/dotnet vstest tests/AFKtor.Tests/bin/Debug/net10.0/AFKtor.Tests.dll '--TestCaseFilter:FullyQualifiedName~InvalidEnumsCannotSmuggleTextAndParallelProducersReportLoss'
```

Result: 1 failed; `Expected: greater than 1`, `But was: 1`; assertion at line 40. The run completed in approximately two seconds.

**Suggested fix:** Separate invalid-input accounting from contention coverage, and arrange an actual held collector critical section with a coordinated second producer before asserting a contention loss. Use a narrowly scoped test seam or another deterministic synchronization fixture; do not depend on scheduling luck or merely increase iteration counts. Verify the focused test both with the default processor count and `DOTNET_PROCESSOR_COUNT=1`.

### Critical

None identified.

### Minor

None separately reported.

## Evidence and review limits

- Read the scoped review contract first, followed by the task brief, implementer report, and generated commit/stat/full-diff package. The package was the primary change view. Read repository guidance, glossary, feature spec, ADR, and the new API documentation. No unrelated implementation exploration was needed.
- Confirmed workspace HEAD equals the reviewed commit. Read supplied `task-1-full-test.log`: 106 passed, 0 failed, 0 skipped. Read `task-1-build.log`: Windows Release cross-build succeeded with 0 warnings and 0 errors. Read the runtime/source instrumentation RED evidence. These are recorded implementer runs, not independently rerun full validations.
- Ran only the focused reproduction above because the contention assertion had a specific unanswered scheduling assumption. No source, test, branch, or index edits were made; this review report is the only intentional written artifact.
- **Cannot verify here:** native Windows startup, effective SID/ACL behavior, installed-runtime packaging, and live League transitions. Cross-compilation does not establish these runtime properties; the implementation report and docs correctly leave them pending.
- **Outside this ticket / not verified as delivered:** durable prior-run history, seven-day and 20,000,000-byte persisted retention/recovery, and the later ready-check/settings/operation evidence. The live slice correctly labels those unavailable or memory-only. Their integration and full cross-ticket contracts still need the planned later reviews.
