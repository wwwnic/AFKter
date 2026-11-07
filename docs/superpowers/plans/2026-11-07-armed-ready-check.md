# Armed Ready-check Acceptance Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Implement ticket 02: start disarmed, let the player arm auto-accept from the tray, and accept each eligible ready check once after three seconds.

**Architecture:** Extend the existing .NET 10 WinForms application. Keep LCU discovery and authenticated transport separate from a UI-independent acceptance controller. Deliver ready-check updates through a connection-scoped event stream, use a controllable clock for scheduling, and revalidate the current check through HTTP before the one permitted POST.

**Tech Stack:** C# / .NET 10, WinForms, HttpClient, ClientWebSocket, NUnit 4.

**Spec:** [.scratch/ready-check-auto-accept/spec.html](../../../.scratch/ready-check-auto-accept/spec.html)

**Ticket:** [02 — Armed ready-check acceptance](../../../.scratch/ready-check-auto-accept/issues/02-armed-ready-check-acceptance.html)

## Global Constraints

- Use C# on .NET 10 with WinForms; retain SDK 10.0.400 and warnings as errors.
- AFKtor starts disarmed. Arming survives League restarts and resets on AFKtor restart.
- Use a fixed three-second delay for this ticket; persisted configurable delay belongs to ticket 03.
- No accept for a check first detected while disarmed, including arming halfway through that check.
- Duplicate events must neither reset the deadline nor cause duplicate POSTs.
- Disarm, decline, expiration, disconnect, or replacement of the current connection cancels pending work.
- Preserve loopback-only destinations, disabled redirects/proxies, and credential secrecy.
- Ticket 01's live Windows/League verification is still pending. Automated tests do not complete that requirement.

## Review Focus

- A disarm/rearm cycle during one check must not revive its canceled work.
- Old connection events and late HTTP responses must not act on a new connection.
- A check that ends or is replaced while final verification is in flight must not be accepted.
- A POST timeout has an ambiguous outcome; do not retry the same check automatically.
- Malformed events, event-stream loss, and shutdown must fail closed without crashing the tray.

## Task 1: Connection-scoped LCU ready-check transport

**Files:** Add `src/AFKtor.Core/LcuConnection.cs`, `LcuReadyCheckClient.cs`, and matching tests. Extract shared lockfile validation/authentication from `LcuConnectionProbe.cs` without changing its existing behavior.

**Interfaces:** An immutable `LcuConnection` describes validated connection credentials without exposing them in logs. `LcuReadyCheckClient` reads the ready-check snapshot, streams typed updates, and posts acceptance on that same connection. A session generation is separate from server payload fields and changes on reconnect.

- [x] Check the published schema and JoinGameAfk reference for `/lol-matchmaking/v1/ready-check`, its event topic and payload, and `POST /lol-matchmaking/v1/ready-check/accept`. Record the observed identity/lifecycle fields before choosing the typed snapshot model; do not invent a server-provided check ID.
- [x] Write failing HTTP-handler and event-decoder tests for authentication, valid pending/declined/accepted/expired states, empty or malformed payloads, deletions, non-success responses, cancellation, and stream closure. Verify fixed loopback URLs and the body/method of acceptance.
- [x] Run `dotnet test tests/AFKtor.Tests --filter FullyQualifiedName~LcuReadyCheck` and confirm the new tests fail for the missing behavior.
- [x] Implement the transport, preserving existing probe tests. Subscribe before seeding current state so initial detection does not leave an event gap; invalidate the entire session when its event stream is lost. Reconcile snapshots and updates conservatively, discarding stale results.
- [x] Run the new and existing transport tests; all must pass. A reconnect must never reuse a pending operation from the previous generation.

## Task 2: Deterministic once-per-check acceptance controller

**Files:** Add `src/AFKtor.Core/ReadyCheckController.cs` and `tests/AFKtor.Tests/ReadyCheckControllerTests.cs`.

**Interfaces:** `ReadyCheckController` consumes typed connection-scoped updates and a clock (`TimeProvider` or an injected delay), exposes arming/countdown state, and invokes the transport's read/accept operations. Public operations include `SetArmed(bool)` and a session invalidation/update entry point. Serialize state transitions and validate the operation generation after every await.

- [x] Write failing tests with a manually advanced clock: no early accept at 2.999 seconds; exactly one at three seconds; duplicate pending events do not reset the deadline; no action when first seen disarmed; no action after disarm/rearm during the same check.
- [x] Add tests for decline, expiration, disconnect, connection replacement, accepted response, stale events, final GET failure, replaced check during final GET, and exit during a countdown. Test a failed/ambiguous POST is attempted only once. A new check after reconnect is eligible while arming remains enabled.
- [x] Run the controller tests and confirm they fail before implementation.
- [x] Implement per-session check lifecycle tracking and cancellation. On deadline, GET the current state, confirm it is still the same pending check on the current connection and still armed, then mark its attempt consumed before issuing POST. Never automatically retry a consumed check.
- [x] Run controller tests; require all tests to pass without real sleeps or League.

## Task 3: Tray integration and handoff validation

**Files:** Modify `src/AFKtor.Tray/TrayApplicationContext.cs`, `README.md`, and ticket 02's HTML comments. Add a focused session coordinator in Core if needed to test UI-independent reconnect orchestration.

**Interfaces:** Retain existing League discovery. Use a single owned session lifetime for probe, stream, controller, and shutdown. Marshal status changes to the WinForms thread and await all owned work before disposal.

- [x] Add integration tests for startup disarmed, preserving arming across replacement sessions, suppressing old-session callbacks, and cancellation during shutdown.
- [x] Add a checked **Arm auto-accept** tray item and an auto-accept status/countdown item. Keep connection status and Exit. Disarming cancels synchronously before rendering new status; never persist arming.
- [x] Document the menu, fixed three-second delay, failure behavior, exact test/build commands, and manual Windows/League acceptance procedure.
- [x] Run `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true` and `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true` in this Linux environment. Require zero failed tests and zero build warnings/errors.
- [x] Review the entire diff against all ticket acceptance criteria and the five review-focus conditions; resolve defects and rerun affected checks.
- [ ] On Windows with League installed, verify actual events and accept route, decline/disarm before deadline, expiration, reconnect while armed, and restart AFKtor disarmed. If unavailable here, report that limitation explicitly and retain this as pending validation in the ticket.

## Starting evidence

- Main has no application files; base is `origin/codex/ticket-1-connection-status` at `f669b04503b09ec1ca5990323980287c653f8686`.
- Working branch: `codex/ticket-2-armed-ready-check`.
- Baseline on 2026-11-07: `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true` passed all 14 tests.
- No product code changed during planning.

## Implementation results (2026-11-07)

- Automated implementation complete: 62 NUnit tests pass, including actual loopback TLS/WSS transport, deterministic countdowns, cancellation, reconnect ordering, malformed-data recovery, and synchronization-context-safe shutdown.
- Independent review found four important defects; each was reproduced by failing regression tests and fixed. No critical findings. Minor deferred: `ReadyCheckUpdate.IsCreate` remains available but does not drive eligibility; repeated Create frames are not trusted as unique check identifiers.
- Connection initialization is conservative for both HTTP seeds and first events. A first pending event cannot bypass the seed's eligibility rule.
- Final verification compares the server timer with both the last event and the elapsed monotonic local countdown. Insufficient or backwards timing evidence skips acceptance rather than guessing check identity. This can require manual acceptance if the installed client's timer is stale.
- Windows tray screenshots and installed-League route/event verification remain pending; this environment is Linux with no Windows Desktop runtime. Follow the README's manual acceptance steps.
