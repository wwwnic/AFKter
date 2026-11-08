# Existing JoinGameAfk mock — Linux smoke evaluation

On November 8, 2026, the player selected a lightweight development approach:
reuse their existing JoinGameAfk mock for routine checks, keep focused automated
verification, and defer real-client testing until they use the app while playing.
Native testing should not block ordinary development or be repeated after every
small change. Native acceptance remains a separate, honest evidence claim.

## What actually ran

JoinGameAfk revision `674d092` already contains a WPF mock client with a separate
ASP.NET Core HTTPS/WebSocket server and simulation state. A temporary headless
console runner linked those existing source files and its Common project,
referenced Afkter Core at `5d4b6c8`, and ran successfully on Linux with .NET 10.
No simulator code was copied into Afkter. Neither production application was
modified. The original mock has no headless entry point; the temporary runner
supplies that launcher and a short-lived localhost certificate only.

The existing sample `short-champion-select-flow-cell1-open.yaml` loaded into the
original simulation state. This is a supplied sample scenario, not evidence of a
captured real-client session. No separate recorded game captures were found in
the inspected repository files.

Observed results through actual localhost HTTPS requests:

| Check | Result |
| --- | --- |
| Start original mock server on Linux | Passed |
| HTTPS connection | HTTP 200 |
| Afkter idle ready-check read | Inactive/Invalid |
| Afkter idle draft read | No draft, as expected |
| WSS subscription/event delivery | Ready-check event received |
| Afkter accept POST | Mock changed response to Accepted |
| Afkter ban PATCH, champion 122 | Accepted; mock action completed with 122 |
| Afkter hover PATCH, champion 86 | Accepted; mock action selected 86, incomplete |
| Afkter lock PATCH, champion 86 | Accepted; mock action completed with 86 |
| Afkter active ready-check parsing | Blocked: mock omits numeric `timer` |
| Afkter planning/draft parsing | Blocked: gameflow omits `gameData.gameId` |
| GET bannable-champion-ids | HTTP 404: route absent |
| GET pickable-champion-ids | HTTP 404: route absent |

Additional source inspection shows champion-select payloads also omit `gameId`,
`isCustomGame`, and `isSpectating`, which Afkter's current parser requires.
The mock advertises a token but does not validate Authorization, so successful
connections do not verify credential rejection. Request credentials used here
were disposable mock values, not a real League account.

## Meaning and next useful step

This establishes that the existing simulator can run headlessly here and that
Afkter's real transport can send commands the original mock understands. It does
**not** establish a complete autonomous queue-to-draft journey: acceptance was
called directly through the HTTP adapter, and ban/pick writes were invoked
against actions selected by the harness. The normal controllers cannot consume
the current mock's incomplete read contract unchanged. The WinForms tray,
Windows process discovery and real-client timing were not exercised.

The next useful increment is a small compatibility addition to the existing
mock (or a clearly identified external bridge): supply lifecycle IDs/flags,
ready-check elapsed time, and availability endpoints. Then exercise Afkter's
normal monitors/executors against its original scenario playback. This avoids
porting the mock into Afkter or building a second simulator. Do not weaken
production identity/timer checks merely to accept incomplete mock payloads.

The reusable launcher and output remain outside the app repository at
`/workspace/shared/afkter-existing-mock-smoke/` (`Smoke.csproj`, `Program.cs`,
`README.md`, and `results.txt`). Its README contains the exact run command.
These shared workspace artifacts are local, not committed application sources;
this report is the durable repository record.

No production settings or shipped draft-write gates changed. Ticket 07's original
native acceptance criteria are not marked complete by this smoke evaluation.
