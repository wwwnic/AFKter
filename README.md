# AFKtor

AFKtor is a Windows tray companion for League of Legends. Ticket 01 implements
connection status and automatic reconnection. Ticket 02 adds session-only arming
and one delayed auto-accept attempt per eligible ready check.

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

The app has no main window. Find AFKtor in the notification area (including the
hidden-icons flyout), hover for connection status, or right-click for status and
**Arm auto-accept**, the active countdown, and **Exit**. Closing the terminal is not the normal tray shutdown path; use Exit.

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

AFKtor starts **disarmed**. Use **Arm auto-accept** in the tray menu to enable it
for this AFKtor session. A newly detected unanswered ready check starts a fixed
**3-second** countdown, shown in the tray menu. AFKtor then re-reads its state and
sends one accept request only if it remains eligible. Duplicate events do not
restart the countdown. A failed or timed-out POST is never retried for that check;
check League and accept manually if necessary.

Disarming, accepting/declining in League, the check ending, event-stream loss,
or changed League credentials cancels pending work. Disarming and rearming does
not revive the same check. Checks detected disarmed are skipped. Arming survives
League restarts; restarting AFKtor resets arming. No arming state is saved.

The LCU resource has no check ID. AFKtor tracks uninterrupted check lifecycles
within one event-stream session and treats a backwards elapsed timer as uncertain.
The final timer must also be compatible with the locally elapsed countdown;
insufficient timer evidence skips that check rather than guessing its identity.
The first observation on each connection establishes a baseline. If it is
already pending (whether an event or the seed GET arrives first), it is skipped; accept that one
manually. After reconnecting, the next newly observed check is eligible. These
conservative skips prevent accepting an ambiguously identified check.

Disarm or Exit cancels work before dispatch. Once an HTTP accept request has
been sent, cancellation cannot undo the action in League. A configurable delay
and persisted settings belong to ticket 03.

## Connection behavior

AFKtor starts disconnected and checks every two seconds, without overlapping
requests. It finds `LeagueClient.exe`, reads the adjacent lockfile with shared
read access, validates its process ID, and re-reads it on each check. This supports
custom install directories and refreshed credentials after client restarts.

AFKtor opens an authenticated WSS event stream and reads
`/lol-matchmaking/v1/ready-check`. A successful snapshot or event marks the
session connected. It subscribes to `OnJsonApiEvent_lol-matchmaking_v1_ready-check`
and revalidates through HTTP before `POST /lol-matchmaking/v1/ready-check/accept`.
The initial GET never overwrites a newer event. Stream closure, malformed data,
missing credentials, authentication failures, and request timeouts invalidate the
session. Discovery retries every two seconds without overlapping; HTTP and
connection attempts have three-second timeouts. WebSocket keepalive uses a
one-second interval and three-second timeout to detect an unresponsive stream.
AFKtor must run as the same Windows user and with sufficient access to
inspect League; an inaccessible client remains disconnected.

LCU uses a self-signed certificate. HTTP and WebSocket share the dedicated handler. Certificate validation is relaxed only in
the dedicated loopback HTTP handler. Redirects and proxies are disabled, the
destination is fixed to `127.0.0.1`, and credentials are never logged or saved.
Endpoint reference: [LCU schema](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json).

## Manual acceptance check

The automated NUnit suite includes simulated events with a manually advanced
clock, stale-response/cancellation tests, and an actual loopback TLS/WSS server
that checks authentication, event subscription, GET, and the bodyless accept POST.
This Linux environment cross-compiles the Windows target with:

```sh
dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
```

On Windows with the installed League Client:

1. Close League, then start AFKtor. Verify tooltip/menu say **League: Disconnected**.
2. Launch League normally. After the LCU API becomes available, verify **League: Connected**.
3. Exit League. Verify **Disconnected** within roughly five seconds.
4. Relaunch League. Verify **Connected** without restarting AFKtor.
5. Use AFKtor's **Exit** while connected or while League is starting. Verify the
   icon disappears and the process exits.

6. Start AFKtor again and verify **Arm auto-accept** is unchecked. Receive a
   ready check while disarmed: AFKtor must not accept it. Arming during that
   check must not start a countdown.
7. Arm before the next check. Verify the menu countdown starts at three seconds
   and League receives one accept at the deadline. Repeated pending updates must
   not reset the deadline or send extra requests (also covered by simulation).
8. On separate checks, disarm during the countdown, decline in League, and let
   the check expire. Verify each cancels acceptance. Disarming/rearming during
   the same check must leave it skipped.
9. Restart League while armed with a countdown running. Verify old work is
   canceled and arming stays enabled. Accept any check already pending when
   reconnecting manually; the next new check must be accepted once.
10. Verify Exit during a countdown stops the process without accepting. Restart
    AFKtor and verify it is disarmed again.

Routes and payloads are verified against the published schema and JoinGameAfk
reference. The installed client's events and acceptance route still need live
verification on Windows. Record the client version and results when performing
these steps.

## Conventions

C# uses four spaces, file-scoped namespaces, PascalCase types/methods/properties,
camelCase locals/parameters/private fields, and `Async` suffixes on production async
methods. Test methods use descriptive behavior names without an `Async` suffix.
Keep discovery, LCU communication and UI separate. `.editorconfig` records
indentation; no additional formatter or linter is required.
