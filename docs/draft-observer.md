# Read-only draft observer — ticket 01

AFKtor now samples draft state alongside ready-check monitoring. The tray menu
shows admission, queue, assigned role, local active action ID/type, and the last
observed timer. Every draft status explicitly says **read only**. `Admitted`
means a supported lifecycle was observed from its beginning while enabled; it is
not an authorization to write, nor proof of installed-client compatibility.
This ticket sends no draft action requests. Ready-check acceptance is unchanged.

## State and lifecycle

`DraftObserver` is UI-independent and consumes immutable snapshots. A new AFKtor
process starts disabled. The existing tray arming item enables observation
admission too; disabling invalidates admission for that draft. Rearming during
the same draft does not restore it. Enabling requires fresh idle evidence or a
subsequently different authoritative game ID. Enabled state survives successive
matches and League credential changes within the running AFKtor process.

`DraftSessionMonitor` owns serialized reads on the host's two-second discovery
tick. Each credential lifetime gets a new generation; each successful read gets
a strictly increasing sequence. Duplicate/stale sequences and replaced-session
inputs cannot regress state. A connection baseline already in draft is observe
only. A fresh idle baseline followed by a draft, or a different positive game ID
on the same uninterrupted connection, demonstrates a new draft. Read failure,
missing credentials and shutdown invalidate the observation session; reconnect
requires a fresh baseline. Malformed state is disconnected rather than treated
as an idle gap that could accidentally authorize an ongoing draft.

The read path uses authenticated GET `/lol-gameflow/v1/session`, then (only in
`ChampSelect`) GET `/lol-champ-select/v1/session`, then another gameflow GET.
Phase/game ID/queue must agree across reads and the draft's game ID must match.
The two-second poll is intentionally sufficient for this read-only slice; no
WebSocket event arrival order is trusted as authoritative admission evidence.
Polling may miss very brief transitions. A future executor requires current
final verification and cannot treat this sampled timer as a dispatch deadline.

Supported observation admission is normal Summoner's Rift Draft Pick queue 400,
with assigned `top`, `jungle`, `middle`, `bottom`, or `utility` role. Unknown role,
other queue, custom/spectator mode, infinite/invalid timer or ambiguous local
window cannot gain action admission. The local action is selected by
`actorCellId == localPlayerCellId`, `isInProgress`, and `!completed`; identity is
`(generation, gameId, action.id)`, not champion or array position. Teammate
`championPickIntent` is distinct from a champion selected in a ban action.
No champion availability/fallback decisions are implemented here.

## Evidence and reference limits

Research performed November 8, 2026:

- [Riot queue catalog](https://static.developer.riotgames.com/docs/lol/queues.json)
  maps 400 to 5v5 Draft Pick; 420 and 440 are ranked, 430 is blind pick.
  Download SHA-256: `d2578dab3a0616443286da1e3763270c4000b6f4b0a4e21fd32e45e0e97fecb1`.
- [Published LCU schema](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json)
  defines the two GET endpoints, `ChampSelectSession`,
  `ChampSelectPlayerSelection`, `ChampSelectTimer`,
  `TeamBuilderDirect-ChampSelectAction`, and gameflow `gameData.queue.id`.
  Schema download SHA-256:
  `a637b5ce7737c193337a1ba2456aa22790cb0c518de507023c21b5c278866f76`.
  Its `info.version` is only `1.0.0`, not an installed League build number.
- [Pinned JoinGameAfk session reference](https://github.com/wwwnic/JoinGameAfk/blob/674d0927303782e46d56a7efffd819147192358b/JoinGameAfk.Plugin/Phase/ChampionSelect/ChampSelect.Session.cs)
  confirms nested action groups, active local actions and the five role strings.
  Its [timer code](https://github.com/wwwnic/JoinGameAfk/blob/674d0927303782e46d56a7efffd819147192358b/JoinGameAfk.Plugin/Phase/ChampionSelect/ChampSelect.Timer.cs)
  interprets `adjustedTimeLeftInPhase` as milliseconds; its helper uses positive
  `gameId` as draft identity. The schema describes adjusted time as sync-adjusted
  phase time, so AFKtor reports the supplied value without inventing an epoch
  deadline. Infinite timers are observe only.

The fixture under `tests/AFKtor.Tests/Fixtures/draft/ban.json` is synthetic and
schema-derived, not a captured installed-client session. Replay tests exercise
ban → pick → completed → ended → next game, roles, teammate intent vs ban
selection, malformed snapshots, duplicate/out-of-order inputs, baseline admission,
credential replacement, shutdown during reads, and coherent GET ordering. The
existing suite protects ready checks. These sources support a conservative
read-only implementation; they do not establish live API compatibility.

## Diagnostic contract

The existing token-authenticated read-only `/v1/state` projection adds `draft`;
`draft.observed` events carry `details.draft`. Fields are fixed enums and numeric
IDs/timers only: generation, observation sequence, game ID, queue ID, role,
admission, local action ID/type, and observed milliseconds. No account names,
PUUIDs, credentials or raw authenticated payloads are retained. Existing 1,024
in-memory event, 256 sink queue, seven-day and 20 MB history bounds apply.
Diagnostics use nonblocking ingress; dropping evidence never blocks observation.
A draft field may be null before the observer publishes its initial state.

## Native gates still required

No Windows desktop or installed League Client is available in this environment.
Cross-compilation and simulated HTTP/replay tests cannot close these gates:

1. Record installed League version, region, queue 400 identity, and the real
   gameflow/champ-select payload shapes (including consistent positive game ID
   before draft, roles on both sides, action IDs across phases and match restart).
2. Observe planning, simultaneous bans, teammate play hovers vs ban selections,
   local pick turn, completed pick, finalization, dodge and next draft. Check
   `adjustedTimeLeftInPhase` units/resets and infinite semantics against League.
3. On Windows, inspect menu status/timer readability and refresh cadence. Verify
   disarm/rearm, enabling already in draft, League credential restart, disconnect
   and AFKtor Exit. An ongoing/reconnected draft must stay observe only until a
   demonstrably new draft; AFKtor restart must be disabled.
4. Capture sanitized read-only evidence and confirm **zero draft writes**, while
   existing armed ready-check acceptance continues to behave as before.

Do not enable draft writes until the installed-client semantics are verified.
