# Agent Diagnostics Execution Plan

> Use superpowers:subagent-driven-development. User authorized execution of all four published tickets with a fresh implementer and a separate reviewer per ticket, then a fresh whole-feature review.

**Goal:** Implement the accepted agent diagnostics design across Linux-tested core/services and the Windows tray app.

**Spec:** [.scratch/agent-diagnostics/spec.html](../../../.scratch/agent-diagnostics/spec.html)

**Architecture:** Add typed source-level diagnostic observations, coherent in-memory projections, bounded asynchronous history, and an authenticated read-only loopback API. Keep observers isolated from automation and preserve existing behavior.

## Global Constraints

- .NET 10; Linux unit/integration tests; Windows tray app. Keep warnings as errors.
- Retain no queryable history older than seven days; history disk budget 20,000,000 bytes across active, retained and damaged app-managed history files.
- Read-only API, 127.0.0.1 assigned port, fresh at-least-256-bit bearer token, restricted per-instance discovery.
- Diagnostics cannot block or change acceptance, cancellation, discovery, or app shutdown; two-second diagnostic shutdown budget.
- Bound memory, queueing, details, pagination, responses, concurrency, deadlines and retries. Explicitly expose loss/gaps/truncation/degraded health.
- No lockfile contents, credentials, raw authenticated bodies, unchecked exception messages, diagnostic tokens in history, or machine-specific absolute paths.
- No new automation, visual viewer, or remote-access bridge. Preserve conservative check identity/timing and once-only requests.

## Review Focus

- Diagnostic failure or saturation must leave scheduling/cancellation and shutdown unchanged.
- Current memory/disk pagination must not duplicate records or conceal gaps.
- Retention must count actual encoded bytes and recover safely after restart/corruption.
- API discovery/auth/lifetime must match the actual running instance on Linux and Windows.
- Evidence must distinguish a dispatched or HTTP-completed request from confirmed League acceptance.

## Tasks

1. Implement [ticket 01](../../../.scratch/agent-diagnostics/issues/01-query-live-app-and-connection-diagnostics.html): full live-query slice. Establish contracts and extension seams; verify, commit, independent scoped review, resolve findings.
2. Implement [ticket 02](../../../.scratch/agent-diagnostics/issues/02-query-retained-history-across-restarts.html): durable retained history and recovery through API. Reuse ticket 01 contracts; verify, commit, independent scoped review, resolve findings.
3. Implement [ticket 03](../../../.scratch/agent-diagnostics/issues/03-explain-ready-check-decisions-and-accept-attempts.html): controller evidence through API. Reuse generic model/store; verify, commit, independent scoped review, resolve findings.
4. Implement [ticket 04](../../../.scratch/agent-diagnostics/issues/04-explain-startup-settings-and-lcu-failures.html): remaining source-level evidence through API. Verify, commit, independent scoped review, resolve findings.

Tickets 02–04 each logically depend only on 01. Execute sequentially in this shared checkout to avoid overlapping source edits. No standalone prefactor needed. Each task brief contains its full ticket and tooling instructions; exact public interfaces and finite implementation limits are settled by ticket 01 and recorded for downstream agents.

## Final verification and integration

Fresh whole-feature review against baseline 7118f9e; resolve actionable findings, run full Linux tests and warning-free Windows Release cross-build, record evidence and native verification limits. Keep the no-PR/direct-main preference already established by the user; integrate and push only after review/verification pass.

## User-requested pause before ticket 03

Tickets 01 and 02 are implemented and independently reviewed; the Important
findings were corrected and scoped fixes reviewed. One ticket 02 Minor remains:
unknown prior-run queries can report 503 rather than 404 when only writing is
disabled. Tickets 03/04 are not started and must wait for a user request to resume.
Current work is committed and pushed to the feature branch, without merging main.
See the [checkpoint and resume record](../../reviews/2026-11-07-agent-diagnostics-checkpoint.md).
A final whole-feature review is still required once all four tickets are done.
