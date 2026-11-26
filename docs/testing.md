# Testing without League

AFKtor ships a fake League client (`FakeLcuServer`) so the whole accept flow can be
tested without League, a Riot account or Windows.

## What the fake is

`tests/AFKtor.FakeLcu/FakeLcuServer.cs` serves the parts of the League Client API
(LCU) that AFKtor uses, the way League serves them:

- HTTPS and the WAMP event socket on `127.0.0.1`, with a fresh self-signed
  certificate, port and password on every start.
- Basic auth as `riot:<password>` on every request, including the socket upgrade.
  Wrong credentials get `401` and are counted in `UnauthorizedCount`.
- `GET /lol-matchmaking/v1/ready-check` (404 with League's error body when no check
  is showing), `POST /lol-matchmaking/v1/ready-check/accept` (204) and
  `GET /lol-gameflow/v1/gameflow-phase`.
- Events only for topics a client subscribed to with `[5, "<topic>"]`, framed as
  `[8, "<topic>", {data, eventType, uri}]`, with Create, Update and Delete event types.
- Ready-check payloads with League's fields (`declinerIds`, `dodgeWarning`,
  `playerResponse`, `state`, `suppressUx`, `timer`). `timer` counts whole seconds
  since the check appeared.

Scenario steps move it through a queue: `SetPhaseAsync`, `StartReadyCheckAsync`,
`AnswerInClientAsync`, `EveryoneReadyAsync`, `DeclinedByOtherPlayerAsync`,
`ExpireAsync`, `DropEventStreams`, `RestartAsync`, and `FailAccepts` to make the
accept POST fail.

Payload shapes follow [JoinGameAfk's mock client](https://github.com/wwwnic/JoinGameAfk)
(MIT) and the community [LCU schema](https://github.com/KebsCS/lcu-and-riotclient-api).
No code was copied. Unlike JoinGameAfk's mock, this fake sends each event under the
topic the client subscribed to and always includes `timer`, which AFKtor requires.

## End-to-end tests

`tests/AFKtor.Tests/FakeLcuEndToEndTests.cs` runs the real Core pipeline
(`ReadyCheckSessionMonitor`, `ReadyCheckController`, `LcuReadyCheckClient`,
`MatchStatusTracker`) and the tray app's own `LcuHttp.CreateClient()` against the fake.

```sh
dotnet test tests/AFKtor.Tests --filter FullyQualifiedName~FakeLcuEndToEndTests
```

They cover: accepting a fresh check and reporting champ select, waiting for the
delay, another player declining and the next check still being accepted, a check
missed while disarmed, disarming or accepting in League during the countdown, a
check already showing when AFKtor connects, League restarting on a new port and
password, the event stream dropping, a failed accept never being retried, and a
stale password never connecting.

## Driving the real tray app

`tools/AFKtor.FakeLcu.Cli` runs the fake as a console app and writes its lockfile.
Two environment variables point the tray app at it:

- `AFKTOR_LCU_LOCKFILE`: read this lockfile instead of finding the `LeagueClient`
  process. It is used only while the process it names is running.
- `AFKTOR_DATA_DIR`: keep settings, diagnostics and `errors.log` here instead of
  `%LOCALAPPDATA%\AFKtor`, so test runs don't touch your real settings.

Exit any normal AFKtor first, since only one copy runs at a time. Then, in PowerShell:

```powershell
# Window 1: the fake League client. It prints the lines to paste into window 2.
dotnet run --project tools/AFKtor.FakeLcu.Cli
```

```powershell
# Window 2: AFKtor, pointed at the fake.
$env:AFKTOR_LCU_LOCKFILE = "<path printed by window 1>"
$env:AFKTOR_DATA_DIR = "$env:TEMP\AFKtor-fake"
dotnet run --project src/AFKtor.Tray
```

Type a command and Enter in window 1:

| Key | League does |
| --- | --- |
| `q` | Enters the queue (Matchmaking) |
| `r` | Finds a match: the ready check appears |
| `a` / `d` | You click Accept / Decline in League |
| `e` | Everyone accepted: champ select |
| `o` | Another player declined: back in queue |
| `x` | The check times out: out of queue |
| `g` / `c` / `l` | Game in progress / needs reconnect / back to lobby |
| `k` | League restarts with a new port and password |
| `s` | The event stream drops |
| `f` | Accept requests start (or stop) failing |
| `?` | Shows the phase, accept count and the ready-check resource |

A typical run: arm AFKtor from the tray menu, then `q`, `r`, watch the countdown in
the menu and `accepts 1` under `?`, then `e` and check the tray says
**Match accepted — in champ select**.

Live League checks that the fake can't replace are listed under
[Manual acceptance check](../README.md#manual-acceptance-check). Co-op vs AI is the
lowest-stakes queue for them. Practice Tool and custom games never show a ready check.
