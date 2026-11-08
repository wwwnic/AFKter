# Planned bans — ticket 03

The ban pipeline is implemented and exercised through the real HTTP adapter with
simulated League responses. **The shipped tray still sends zero draft writes.**
`TrayApplicationContext` passes the internal `nativeSemanticsVerified: false` gate;
it is not a player setting. An admitted ban window displays “waiting for
installed-client verification” and publishes `NativeVerificationRequired` through
diagnostics. Ready-check acceptance is unchanged. Picks and the default watchdog are documented separately in [planned-picks.md](planned-picks.md).

## Decision and execution

The assigned role's latest effective in-memory ban list supplies the only candidates.
The executor reads GET `/lol-champ-select/v1/bannable-champion-ids`, then a fresh
coherent gameflow/champ-select/gameflow snapshot immediately before dispatch.
Unavailable candidates, both teams' recorded bans/completed ban actions, and
teammate play intent or selected picks are skipped in order. A teammate's ban hover
alone is not protected. An empty/exhausted list produces evidence and no request;
a later edit or availability change is evaluated again.

Timing is now live and configurable; see [live timing](live-timing.md). With author defaults a local ban window normally waits at least **1,000 ms** from first detection.
Repeated observations and live edits do not restart that monotonic clock. At
**3,000 ms or less** observed remaining time it skips pacing. Verification time is
subtracted conservatively from the final supplied timer; at **250 ms or less**
remaining budget it sends nothing. These are fixed internal initial defaults,
not empirically validated League timings or new advanced settings. The existing
two-second host poll means normal dispatch occurs on the first refresh after the
pacing interval; this is best effort, not a hard deadline scheduler.

The single execution owner sends PATCH
`/lol-champ-select/v1/session/actions/{id}` with `{championId, completed:true}`.
This ticket completes a ban directly after pacing; it does not add pick hovering.
The host's role-plan revision gate orders request start against publication of a
new plan. Edits after dispatch cannot recall a sent request. Disarm, disconnect,
shutdown, admission loss, game/action replacement cancel the observer's work token;
a late verification cannot dispatch. Reconnect still needs fresh lifecycle
admission from ticket 01.

HTTP acceptance is `AwaitingOutcome`, never confirmation. A later matching completed
local ban confirms `Completed`. A transport failure, timeout or server failure may
have applied the request and remains ambiguous until observation. Even explicit
client-side rejection is not automatically retried in the same action window.
No request is repeated while reconciling, including after rapid edits. A local
ban selection without matching own completed-dispatch evidence, including an
ambiguous intermediate selection, yields `ManualControl` for the whole draft.
There is no ban watchdog. Completed bans are never revised.

Observations are sampled, so a manual change that appears and disappears wholly
between reads cannot be detected. Final verification narrows but cannot eliminate
changes between the last read and League applying a request. These limits must be
included in installed-client acceptance.

## Diagnostics

The existing read-only state endpoint adds `ban`; `draft.ban` history events carry
`details.ban`. Status, generation/game/action IDs, effective revision, chosen
champion, request outcome, skip reasons and total skipped count are allowlisted
numeric/enumerated evidence. At most eight skipped candidates are included per
event; the total count preserves the exhaustion size without an unbounded payload.
Existing nonblocking collection, memory/history retention and credential exclusion
remain in force. Cancellation is published immediately by the host. Tray status
shows draft admission and ban progress separately.

## Reference evidence and native enablement gate

Research on November 8, 2026 used the [published LCU schema](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json),
SHA-256 `a637b5ce7737c193337a1ba2456aa22790cb0c518de507023c21b5c278866f76`.
It defines bannable IDs as an integer array, the action PATCH with an int64 action
ID and `TeamBuilderDirect-ChampSelectAction`, and `ChampSelectBannedChampions`.
The [pinned JoinGameAfk HTTP implementation](https://github.com/wwwnic/JoinGameAfk/blob/674d0927303782e46d56a7efffd819147192358b/JoinGameAfk.Plugin/Services/LeagueClientHttp.cs)
uses the same completion body. Published schema and source are reference evidence,
not installed-client verification.

Before changing the internal host gate to true, record Windows/League build,
region and queue 400; sanitized authoritative payload evidence; real bannable-list
and both-team ban behavior; role/action identity and timer units/resets; observed
ban completion/rejection/intermediate acknowledgments; simultaneous bans and
teammate play intent; manual takeover; disarm/reconnect/dodge/action replacement;
and normal/urgent timing and tray/diagnostic agreement. Verify that the partial
PATCH body and own-acknowledgment attribution match that installed client. Retain
all read-only observer gates in [draft-observer.md](draft-observer.md). Changing
the gate requires that evidence and code review; there is no runtime bypass.

No Windows desktop or installed League Client exists in this environment. Native
acceptance remains pending. Automated verification with .NET SDK 10.0.403:

- `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true`: 215/215 passed, including
  25 ban cases (gate enabled/disabled HTTP pipeline, ordering, availability,
  reconciliation, cancellation, manual control, monotonic deadline and diagnostics).
- `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true --no-restore`:
  passed with zero warnings/errors.
