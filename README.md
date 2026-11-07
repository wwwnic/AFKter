# AFKtor

AFKtor is a Windows tray companion for League of Legends. Ticket 01 implements
connection status and automatic reconnection. Auto-accept is not implemented yet.

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
**Exit**. Closing the terminal is not the normal tray shutdown path; use Exit.

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

## Connection behavior

AFKtor starts disconnected and checks every two seconds, without overlapping
requests. It finds `LeagueClient.exe`, reads the adjacent lockfile with shared
read access, validates its process ID, and re-reads it on each check. This supports
custom install directories and refreshed credentials after client restarts.

A successful authenticated HTTPS GET to `/lol-gameflow/v1/gameflow-phase` means
connected. Missing/inaccessible/partial lockfiles, rejected authentication,
network failures and the three-second request timeout mean disconnected and are
retried. Detection normally takes two seconds, or up to five with a timed-out
request. AFKtor must run as the same Windows user and with sufficient access to
inspect League; an inaccessible client remains disconnected.

LCU uses a self-signed certificate. Certificate validation is relaxed only in
the dedicated loopback HTTP handler. Redirects and proxies are disabled, the
destination is fixed to `127.0.0.1`, and credentials are never logged or saved.
Endpoint reference: [LCU schema](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json).

## Manual acceptance check

1. Close League, then start AFKtor. Verify tooltip/menu say **League: Disconnected**.
2. Launch League normally. After the LCU API becomes available, verify **League: Connected**.
3. Exit League. Verify **Disconnected** within roughly five seconds.
4. Relaunch League. Verify **Connected** without restarting AFKtor.
5. Use AFKtor's **Exit** while connected or while League is starting. Verify the
   icon disappears and the process exits.

The endpoint is verified against the published schema. Live League restart
acceptance still requires an installed, running League Client; simulated tests
do not substitute for that check.

## Conventions

C# uses four spaces, file-scoped namespaces, PascalCase types/methods/properties,
camelCase locals/parameters/private fields, and `Async` suffixes on production async
methods. Test methods use descriptive behavior names without an `Async` suffix.
Keep discovery, LCU communication and UI separate. `.editorconfig` records
indentation; no additional formatter or linter is required.
