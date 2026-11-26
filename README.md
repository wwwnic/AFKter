# AFKtor

AFKtor is a Windows tray companion for League of Legends. Ticket 01 implements
connection status and automatic reconnection. Ticket 02 adds session-only arming
and one delayed auto-accept attempt per eligible ready check. Ticket 03 adds
persisted delay settings and countdown lifecycle handling. Match status shows
whether an accepted check became a match, and Settings can arm AFKtor on launch
and start it with Windows.

## Layout

- Entry point: `src/AFKtor.Tray/Program.cs`.
- `src/AFKtor.Tray/`: Avalonia tray UI (`TrayController.cs`, `SettingsWindow.axaml`)
  and running League Client discovery.
- `src/AFKtor.Core/`: UI-independent connection monitor and authenticated LCU probe.
- `tests/AFKtor.Tests/`: NUnit tests, named `*Tests.cs`.
- `tests/AFKtor.FakeLcu/`: a fake League client for end-to-end tests, and
  `tools/AFKtor.FakeLcu.Cli/` to run it by hand ([Testing without League](docs/testing.md)).
- Assets: tray icons are drawn at startup in `TrayIcons.cs`; no external asset files.
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
hidden-icons flyout), hover for connection and match status, or right-click for status and
**Arm auto-accept**, the active countdown, **Settings…**, and **Exit**. Closing the terminal is not the normal tray shutdown path; use Exit.

Only one AFKtor runs per Windows user session; starting it again while it is
running exits immediately. Unexpected errors never stop League monitoring: a
failed refresh or event session is retried on the next two-second tick, and a
failed accept attempt only affects that check. Such errors are recorded (type,
message and stack trace, never credentials) in `%LOCALAPPDATA%\AFKtor\errors.log`,
which is capped at about 1 MB.

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
or manually configured credentials are required. `AFKTOR_LCU_LOCKFILE` and
`AFKTOR_DATA_DIR` exist only for testing against the fake client.

In restricted agent environments, the .NET CLI may require a writable
`DOTNET_CLI_HOME`, `NUGET_PACKAGES` pointing at an existing cache, and
`MSBuildEnableWorkloadResolver=false`. These are tooling workarounds, not app setup.
Build/test host processes may require execution outside the sandbox.

## Auto-accept behavior

AFKtor starts **disarmed** unless **Arm auto-accept when AFKtor starts** is
checked in Settings. Use **Arm auto-accept** in the tray menu to enable it
for this AFKtor session. A newly detected unanswered ready check starts a countdown
using the configured delay (default **3 seconds**), shown in the tray menu. AFKtor then re-reads its state and
sends one accept request only if it remains eligible. Duplicate events do not
restart the countdown. A failed or timed-out POST is never retried for that check;
check League and accept manually if necessary.

Disarming, accepting/declining in League, the check ending, event-stream loss,
or changed League credentials cancels pending work. Disarming and rearming does
not revive the same check. Checks detected disarmed are skipped. Arming survives
League restarts; restarting AFKtor resets arming to the arm-on-launch preference.
Arming changes made from the tray menu are never saved.

The LCU resource has no check ID. AFKtor tracks uninterrupted check lifecycles
within one event-stream session and treats a backwards elapsed timer as uncertain.
The final timer must also be compatible with the locally elapsed countdown;
insufficient timer evidence skips that check rather than guessing its identity.
The first observation on each connection establishes a baseline. If it is
already pending (whether an event or the seed GET arrives first), it is skipped; accept that one
manually. After reconnecting, the next newly observed check is eligible. These
conservative skips prevent accepting an ambiguously identified check.

Disarm or Exit cancels work before dispatch. Once an HTTP accept request has
been sent, cancellation cannot undo the action in League.

## Delay settings

Open **Settings…** from the tray menu. Choose a whole number from **0 to 30
seconds**, then Save. Zero skips the waiting period but still verifies the pending
check before accepting. A delay longer than the ready check's lifetime will not
force acceptance after it ends. Cancel or closing the settings window discards
edits. The window can stay open while the tray monitors League.

Each eligible check captures the delay when it is first detected. Saving a new
delay during a countdown leaves that deadline and its verification threshold
unchanged; the next eligible check uses the new value. The countdown clears
when canceled or ended, and when final verification starts, before sending the
accept request.

The delay and the arm-on-launch preference are saved at
`%LOCALAPPDATA%\AFKtor\settings.json`. Each falls back on its own: a missing,
malformed, or out-of-range delay becomes 3 seconds, and arm on launch is on only
when the file holds an explicit JSON `true`. A failed save shows an error and
keeps the previous effective settings; retry after checking folder write access.
Saves write a temporary file before replacing the old file. The current arming
state and League credentials are never saved.

**Arm auto-accept when AFKtor starts** (off by default) arms AFKtor as it
starts. The normal safety rules still apply: a ready check already pending when
League first connects is skipped, so arming early never accepts a stale check.

**Start AFKtor when I sign in to Windows** adds or removes an `AFKtor` value
under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that starts the
current `AFKtor.Tray.exe`. The checkbox reflects that value live, and saving
while it is checked re-registers the current path (useful after moving the app).
It is disabled when AFKtor runs through `dotnet AFKtor.Tray.dll` or off Windows,
since there is no stable executable to register. If the settings file cannot be
saved, the startup entry is put back as it was.

## Match status

AFKtor also subscribes to `OnJsonApiEvent_lol-gameflow_v1_gameflow-phase` and
reads `/lol-gameflow/v1/gameflow-phase` once per connection. The tray menu and
tooltip show the phase (In queue, Match found, In champ select, In game,
Post-game) and how the last ready check ended:

- **Match accepted — in champ select**: the check was followed by champ select, so
  the match really started.
- **Match didn't start — back in queue**: League returned to matchmaking, usually
  because another player did not accept. AFKtor keeps working.
- **Match missed — out of queue**: League returned to the lobby, so the check was
  not accepted. Requeue in League.
- **Champ select ended — back in queue / out of queue**: champ select ended
  without a game, for example after a dodge.

The tray icon is grey when disconnected, blue when connected, green in champ
select or in game, and amber when you need to act in League (out of queue after a
check, or a reconnect is needed). Phase data is informational only: unreadable or
unknown phases are ignored, a failed phase read never ends the event session, and
the status clears when League disconnects. It never changes what AFKtor accepts.

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
The Avalonia tray project targets plain `net10.0`, so Linux builds and tests it
without Windows targeting:

```sh
dotnet test AFKtor.slnx
dotnet build AFKtor.slnx -c Release
```

Without League, a fake League client exercises the same flows end to end and can
drive the real tray app. See [Testing without League](docs/testing.md).

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
    AFKtor and verify it is disarmed again.

Ticket 03 settings checks:

1. With AFKtor closed, back up any existing settings file and remove it. Start
   AFKtor, open Settings, and verify 3 seconds and an unchecked arming item.
2. Save 5 seconds, exit, and restart. Verify Settings shows 5 and auto-accept is
   still disarmed. Close with Cancel after editing; reopen and verify 5 remains.
3. Arm and receive an eligible check. During its 5-second countdown, save 1
   second. Verify the active countdown keeps its original deadline and the next
   eligible check waits 1 second. Duplicate events must not reset either deadline.
4. Verify the countdown clears on decline, expiration, disarm, disconnect, and
   after the delay while verification or acceptance is in flight.
5. Select 0 and verify a new eligible check is verified and accepted once without
   waiting. Checks seen disarmed or already pending on connection remain skipped.
6. Try 30 seconds and let a check expire sooner: it must cancel without accepting.
7. Make the settings destination unwritable and try Save. Verify an error, the
   old effective delay, and the open settings window; restore write access and
   retry. Restore any backed-up settings afterward.
8. Reopen Settings repeatedly and verify one window. Exit AFKtor while Settings
   is open, including during a countdown; verify the window and process close.

Match status and startup checks:

1. Arm, queue, and let AFKtor accept. Verify the menu shows **Match: Match found**,
   then **Match accepted — in champ select**, and the icon turns green.
2. Decline a check in League. Verify **Match missed — out of queue** and an amber icon.
3. Dodge or leave champ select in a custom/practice flow. Verify **Champ select ended**.
4. Exit League. Verify the match line disappears and the icon turns grey.
5. Check **Arm auto-accept when AFKtor starts**, save, exit, and restart. Verify the
   menu is armed. With a ready check already showing when League connects,
   verify it is skipped.
6. Check **Start AFKtor when I sign in to Windows** from a built `AFKtor.Tray.exe`,
   save, and verify the `AFKtor` Run value. Sign out and in; verify AFKtor starts.
   Uncheck, save, and verify the value is gone.

Native Avalonia tray interactions on Windows and live League acceptance have
**not** been run in this Linux environment. The deterministic settings/controller
tests, a Linux startup run, and a headless Settings window check do not replace
these checks.

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
bounds, test coverage, and native checks. Framework-dependent Windows installs
require the .NET 10 Runtime and ASP.NET Core 10 Runtime. The Avalonia UI no
longer needs the .NET 10 Desktop Runtime.
