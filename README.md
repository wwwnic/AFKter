# Afkter

Afkter is a Windows tray companion for League of Legends. Ticket 01 implements
connection status and automatic reconnection. Ticket 02 adds session-only arming
and one delayed auto-accept attempt per eligible ready check. Ticket 03 adds
persisted delay settings and countdown lifecycle handling.

Read-only draft observation now shows queue, assigned role, local action and lifecycle
admission in the tray and diagnostics. See [observer behavior, API evidence and
remaining native checks](docs/draft-observer.md). The planned-ban pipeline is implemented,
with an explicit shipped gate that keeps draft writes disabled pending installed-client
verification. See [ban behavior and readiness evidence](docs/planned-bans.md).
Planned pick hover/lock and the default manual-hover deadline watchdog are also
implemented behind the same closed gate. Settings now offers live, autosaved
manual-pick deadline policies: Lock current hover (default), Use my role plan,
or Do nothing. See [pick behavior and native checks](docs/planned-picks.md).

The app name is **Afkter**. Existing `AFKtor.*` project, assembly, and namespace
identifiers and the `X-AFKtor-Run-Id` diagnostics header remain unchanged. Saved
settings, role plans, and diagnostics continue using `%LOCALAPPDATA%\AFKtor`
so existing data stays available.

## Layout

- Entry point: `src/AFKtor.Tray/Program.cs`.
- `src/AFKtor.Tray/`: WinForms tray UI and running League Client discovery.
- `src/AFKtor.Core/`: UI-independent connection monitor and authenticated LCU probe.
- `tests/AFKtor.Tests/`: NUnit tests, named `*Tests.cs`.
- Assets: currently Windows built-in `SystemIcons`; no external asset files.
- `docs/`: API references and contributor guidance.

## Setup and commands

Use Windows 10/11 with the **.NET SDK 10.0.400** (x64; later 10.0.4xx patches
are allowed by `global.json`). Install the SDK, not just the runtime. Run these
commands in PowerShell from the repository root:

```powershell
dotnet restore AFKtor.slnx
dotnet build AFKtor.slnx --no-restore
dotnet run --project src/AFKtor.Tray --no-build
```

The app has no main window. Find Afkter in the notification area (including the
hidden-icons flyout), hover for connection status, or right-click for status and
**Arm auto-accept**, the active countdown, **Settings…**, and **Exit**. Closing the terminal is not the normal tray shutdown path; use Exit.

```powershell
# Full test suite
dotnet test AFKtor.slnx --no-restore
# One test file
dotnet test tests/AFKtor.Tests --no-restore --filter FullyQualifiedName~LcuConnectionProbeTests
# One test
dotnet test tests/AFKtor.Tests --no-restore --filter Name=TracksClientStartingExitingAndRestarting
# Release build
dotnet build AFKtor.slnx -c Release --no-restore
```

Compilation is the C# typecheck; warnings are errors. NUnit 4 with the NUnit3
adapter runs deterministic tests without League. There is no coverage threshold.
NuGet access is needed on the first restore. No application environment variables
or manually configured credentials are required.

In restricted agent environments, the .NET CLI may require a writable
`DOTNET_CLI_HOME`, `NUGET_PACKAGES` pointing at an existing cache, and
`MSBuildEnableWorkloadResolver=false`. These are tooling workarounds, not app setup.
Build/test host processes may require execution outside the sandbox.

## Auto-accept behavior

Afkter starts **disarmed**. Use **Arm auto-accept** in the tray menu to enable it
for this Afkter session. A newly detected unanswered ready check starts a countdown
using the configured delay (default **3 seconds**), shown in the tray menu. Afkter then re-reads its state and
sends one accept request only if it remains eligible. Duplicate events do not
restart the countdown. A failed or timed-out POST is never retried for that check;
check League and accept manually if necessary.

Disarming, accepting/declining in League, the check ending, event-stream loss,
or changed League credentials cancels pending work. Disarming and rearming does
not revive the same check. Checks detected disarmed are skipped. Arming survives
League restarts; restarting Afkter resets arming. No arming state is saved.

The LCU resource has no check ID. Afkter tracks uninterrupted check lifecycles
within one event-stream session and treats a backwards elapsed timer as uncertain.
The final timer must also be compatible with the locally elapsed countdown;
insufficient timer evidence skips that check rather than guessing its identity.
The first observation on each connection establishes a baseline. If it is
already pending (whether an event or the seed GET arrives first), it is skipped; accept that one
manually. After reconnecting, the next newly observed check is eligible. These
conservative skips prevent accepting an ambiguously identified check.

Disarm or Exit cancels work before dispatch. Once an HTTP accept request has
been sent, cancellation cannot undo the action in League.

## Live settings

Open **Settings…** from the tray. Enter an accept delay of **0–30 whole
seconds** (default **3**). Valid edits take effect immediately and autosave;
there is no Apply, Save or Cancel operation. Closing retains valid edits.
Unfinished or invalid text displays an error and preserves the effective revision.
Zero starts verification immediately. A delay longer than the ready check's
lifetime cannot force acceptance after the check ends.

Live edits move an undispatched check's due time from its **original detection**.
Shortening past elapsed time starts verification; lengthening moves the countdown.
Duplicate events never restart it. A delay edit during final verification requires
a fresh read before sending, including when the new due time has already elapsed.
Skipped checks remain skipped, and an accept attempt is never repeated. The
countdown clears during verification, in flight, and on cancellation.

**Show advanced timing** starts collapsed. Draft pacing and hover-to-lock waits
support **0–5000 ms** each (defaults **1000 ms**). The urgent threshold supports
**1000–10000 ms** (default **3000 ms**); the request safety margin supports
**250–1000 ms** (default **250 ms**) and must be smaller than the urgent threshold.
Normal work waits from the original action / hover start. Urgency skips avoidable
waits, while the safety margin always blocks writes too close to expiry.
**Restore author timing defaults** resets all five timing values in one revision,
without changing plans or the manual-pick policy. See [timing details](docs/live-timing.md).

All settings share the role-plan immutable revision, serialized autosave and
visible effective/saved revision, failure and retry status. Storage is now
`%LOCALAPPDATA%\AFKtor\role-plans.json`. Existing `settings.json` delay preferences
are read when the revision has no timing member; the first subsequent edit saves
that migrated delay with the complete revision. New timing takes precedence over
the retained legacy file. Missing/malformed legacy delay defaults to 3 seconds.
Malformed new revision files are preserved and visibly rejected as a whole.
Failed saves leave the latest settings effective and retry automatically; restart
recovers only the last successful save. Arming and credentials are never saved.
Afkter always starts disarmed.

## Connection behavior

Afkter starts disconnected and checks every two seconds, without overlapping
requests. It finds `LeagueClient.exe`, reads the adjacent lockfile with shared
read access, validates its process ID, and re-reads it on each check. This supports
custom install directories and refreshed credentials after client restarts.

Afkter opens an authenticated WSS event stream and reads
`/lol-matchmaking/v1/ready-check`. A successful snapshot or event marks the
session connected. It subscribes to `OnJsonApiEvent_lol-matchmaking_v1_ready-check`
and revalidates through HTTP before `POST /lol-matchmaking/v1/ready-check/accept`.
The initial GET never overwrites a newer event. Stream closure, malformed data,
missing credentials, authentication failures, and request timeouts invalidate the
session. Discovery retries every two seconds without overlapping; HTTP and
connection attempts have three-second timeouts. WebSocket keepalive uses a
one-second interval and three-second timeout to detect an unresponsive stream.
Afkter must run as the same Windows user and with sufficient access to
inspect League; an inaccessible client remains disconnected.

LCU uses a self-signed certificate. HTTP and WebSocket share the dedicated handler. Certificate validation is relaxed only in
the dedicated loopback HTTP handler. Redirects and proxies are disabled, the
destination is fixed to `127.0.0.1`, and credentials are never logged or saved.
Endpoint reference: [LCU schema](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json).

## Manual acceptance check

For lightweight development without League running, see the
[LCU mock and replay tools](docs/lcu-api.md#mock-client-and-scenario-replay),
including the external **lol-testbed** project and its current compatibility gaps.

The automated NUnit suite includes simulated events with a manually advanced
clock, stale-response/cancellation tests, and an actual loopback TLS/WSS server
that checks authentication, event subscription, GET, and the bodyless accept POST.
This Linux environment cross-compiles the Windows target with:

```sh
dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
```

On Windows with the installed League Client:

1. Close League, then start Afkter. Verify tooltip/menu say **League: Disconnected**.
2. Launch League normally. After the LCU API becomes available, verify **League: Connected**.
3. Exit League. Verify **Disconnected** within roughly five seconds.
4. Relaunch League. Verify **Connected** without restarting Afkter.
5. Use Afkter's **Exit** while connected or while League is starting. Verify the
   icon disappears and the process exits.

6. Start Afkter again and verify **Arm auto-accept** is unchecked. Receive a
   ready check while disarmed: Afkter must not accept it. Arming during that
   check must not start a countdown.
7. With the default delay selected, arm before the next check. Verify the menu countdown starts at three seconds
   and League receives one accept at the deadline. Repeated pending updates must
   not reset the deadline or send extra requests (also covered by simulation).
8. On separate checks, disarm during the countdown, decline in League, and let
   the check expire. Verify each cancels acceptance. Disarming/rearming during
   the same check must leave it skipped.
9. Restart League while armed with a countdown running. Verify old work is
   canceled and arming stays enabled. Accept any check already pending when
   reconnecting manually; the next new check must be accepted once.
10. Verify Exit during a countdown stops the process without accepting. Restart
    Afkter and verify it is disarmed again.

Ticket 03 settings checks:

1. Back up both storage files. Start with no files; verify delay 3, advanced
   collapsed, author defaults, and disarmed startup. Restore a legacy delay and
   old v1 role plans; verify migration preserves delay, priorities and policy.
2. Enter 5, close/reopen and restart. Verify it persists and startup is disarmed.
   Enter empty, negative and fractional text; effective revision must not change.
3. Arm before a check. During its countdown, lengthen to 30, then shorten past
   elapsed time. Verify original-time countdowns, immediate verification, no
   duplicate attempts, no revived skipped checks, and compatible timer checks.
4. Edit while final verification is held; require a fresh read before sending.
   Confirm decline, expiry, disarm, disconnect and shutdown clear work.
5. Expand advanced timing; exercise zero/maximum pacing and hover waits, urgent
   priority, margin boundaries, rejected unsafe combinations and one-revision
   restore defaults. Check keyboard navigation and normal/high DPI layouts.
6. Make role-plans.json unwritable, edit and reset. Verify current settings remain
   effective with UNSAVED failure, then restore access and check automatic and
   manual retry, reopen/restart, diagnostics agreement and original-file recovery.
7. Reopen Settings repeatedly: one modeless window. Exit with it open during
   countdown or verification and confirm the window and process close.

Native WinForms interactions and live League acceptance have **not** been run in
this Linux environment. The deterministic settings/controller tests and Windows
cross-compilation do not replace these checks.

Routes and payloads are verified against the published schema and JoinGameAfk
reference. The installed client's events and acceptance route still need live
verification on Windows. Record the client version and results when performing
these steps.

The [tickets 1–3 review record](docs/reviews/2026-11-07-tickets-1-3.md) documents
the individual reviews, final combined review, and automated validation.

## Conventions

C# uses four spaces, file-scoped namespaces, PascalCase types/methods/properties,
camelCase locals/parameters/private fields, and `Async` suffixes on production async
methods. Test methods use descriptive behavior names without an `Async` suffix.
Keep discovery, LCU communication and UI separate. `.editorconfig` records
indentation; no additional formatter or linter is required.

## Agent diagnostics

The tray app starts a read-only, token-authenticated loopback service automatically.
See [the V1 discovery and query contract](docs/agent-diagnostics.md) for agent examples,
bounds, test coverage, and native checks. Framework-dependent Windows installs now
require ASP.NET Core 10 Runtime alongside .NET 10 Desktop Runtime.

Role-plan editing and autosave, champion catalog provenance, recovery behavior,
and pending native acceptance steps are documented in [Role plans](docs/role-plans.md).
