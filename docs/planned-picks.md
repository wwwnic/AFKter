# Planned picks — tickets 04–05

Afkter implements ordered pick hover, lock and the default manual-hover watchdog.
The shipped tray explicitly keeps `nativeSemanticsVerified: false` for both picks
and bans. It sends **zero draft writes** until installed-client semantics are
verified. This internal gate is not a player setting. Ready-check acceptance is
unchanged. Manual deadline policy is configurable in Settings (ticket 05); timing is live and configurable in Settings (ticket 06).

The latest effective assigned-role pick plan supplies candidates in order. Before
each write, the executor reads GET `/lol-champ-select/v1/pickable-champion-ids`
and a coherent gameflow/champ-select/gameflow snapshot. It excludes unavailable
champions, both teams' bans, completed ban actions, teammates' selected champions,
opponents' selected champions from `theirTeam`, and other completed pick actions.
Teammate play intent alone does not reserve a pick. An empty/exhausted plan never
creates a fallback; later eligibility changes and live edits are evaluated again.

Normal timing waits **1,000 ms** from local-action detection before a hover PATCH
`/lol-champ-select/v1/session/actions/{id}` with `{championId, completed:false}`.
After a matching own acknowledgment, locking waits at least **1,000 ms** from that
hover request start and sends the same action with `completed:true`. A live plan
edit can replace an acknowledged automatic hover. Unchanged selections retain
their lock wait even if another plan edit publishes a newer revision.
At **3,000 ms or less** remaining, automatic selection completes the first eligible
planned champion directly, skipping the separate hover wait/request. This avoids
spending the host's next polling interval on a preventable urgent wait.
At **250 ms or less** conservatively verified remaining budget, nothing dispatches.

These defaults use the same observed millisecond timer and urgent/minimum
margins as planned bans. They are initial engineering values, not measured League
latencies. The action's monotonic deadline can only shorten: repeated observations,
a supplied timer reset, and edits cannot extend it. Verification latency is
subtracted from the final supplied timer as well. Every identity-validated final
read persists its shorter deadline, including expiry and exhausted-plan exits;
a later timer reset cannot revive that action. The host polls every two seconds;
execution and watchdog scheduling are best effort and can miss short windows.
Native acceptance must assess whether that cadence is adequate before enabling
writes; these timing values do not claim real-time guarantees. See [live timing](live-timing.md) for supported ranges and live edits.

Only one request can be in flight per executor. HTTP success means
`AwaitingOutcome`, not completion. A matching incomplete hover after an accepted
request is an own acknowledgment; matching intermediate accepted lock selections
also do not cause takeover. An observed completed local pick establishes completion
and is never changed. Status and diagnostics publish that action and actual champion;
a differing champion or completion without own accepted selection evidence yields
manual attribution. Rejected, failed and ambiguous requests are not blindly
repeated. An unresolved sent lock remains pending until observation, even if the
player changes hover. A sent hover with a matching ambiguous/rejected acknowledgment
yields manual control; a different observed champion also yields. No second
request races an unresolved request. Edits after request start cannot recall it.

Any observed external hover change pauses normal pick selection for the entire
draft. Editing a plan never clears that state. Shortly before expiry (the same
3,000 ms margin), the configured watchdog policy applies:

- **Lock current hover** (default) re-reads and locks only the current eligible
  hover, including one outside the plan. An empty, unavailable, banned or already
  picked hover produces `InvalidHover` and no substitution.
- **Use my role plan** selects and locks the first currently eligible champion in
  the latest assigned-role plan, even when replacing the manual hover. Exhaustion
  produces `Exhausted`, skipped reasons, and no request.
- **Do nothing** makes no more automatic selections or locks after takeover in
  that draft. Normal role-plan automation still runs before takeover.

The main **Settings…** dialog explains each choice. Choosing a policy immediately
publishes a whole immutable revision and autosaves with role plans; there is no
Apply button. Effective/saved revision, save failure, recovery warning and retry
are visible there. All main and advanced settings now apply live and autosave (ticket 06); closing retains valid edits. Policy/plan edits
never clear manual takeover. Both policy and plan use the same final dispatch gate:
a newer publication invalidates a queued decision; a policy changed during a held
verification is evaluated before dispatch. A sent request is irrevocable and must
be reconciled before another request, regardless of later policy changes. The next admitted draft
clears takeover. Sampling cannot detect changes that appear and disappear between
reads, nor prove attribution when a player chooses exactly the dispatched champion;
ambiguous transport evidence yields conservatively.

Final verification checks current admission/generation/game/action, queue, local
cell, role, enabled epoch, revision, eligibility and monotonic deadline. The
role-plan publication gate orders dispatch start against live edits. Disarm,
disconnect, session/action replacement, draft end and shutdown cancel queued work;
late reads cannot borrow a replacement action's lifetime. No read/write sequence can
eliminate a change between the last read and League applying the request.

The tray includes pick status, selected champion and conservative countdown beside
the observed draft state. Diagnostics `/v1/state` adds `pick`; `draft.pick` events
carry `details.pick`: numeric IDs, revision, status, manual/lock flags, remaining
budget, outcome and skipped reasons. At most eight skipped candidates are retained,
with a total skipped count. Existing nonblocking ingress and history bounds apply;
credentials, account identities and raw responses are excluded.

## Evidence and remaining native gates

Research November 8, 2026 used the published LCU schema linked in
[lcu-api.md](lcu-api.md), SHA-256
`a637b5ce7737c193337a1ba2456aa22790cb0c518de507023c21b5c278866f76`.
It defines pickable IDs as an integer array, action PATCH with an int64 action ID,
`TeamBuilderDirect-ChampSelectAction.championId/completed`, and
`ChampSelectSession.theirTeam` player selections. Timer interpretation follows the
pinned source evidence recorded in [draft-observer.md](draft-observer.md).
Published schema and synthetic HTTP fixtures do not establish installed-client
compatibility.

Windows and League are unavailable here. Before opening the shipped gate, retain
all observer/ban gates and record the installed version, region and queue 400,
sanitized real pickable/selected/banned data from both teams, role/action identity,
timer units/resets, hover acknowledgment vs player changes, intermediate lock
acknowledgments, completion/rejection/ambiguity, manual outside-plan/invalid hover,
normal/urgent timing and poll cadence, edits during hover/verification/in-flight
requests, expiry, disarm, reconnect, dodge and next draft. Confirm tray/diagnostics
agreement. Gate changes require that evidence and code review.

Automated verification uses SDK 10.0.403; deterministic fake-time and simulated
HTTP tests exercise both enabled and disabled gates, ordered eligibility, pacing,
urgent completion, own acknowledgments, takeover, watchdog, live edits, bounded
evidence, cancellation and request serialization. Native acceptance remains pending.

- `dotnet test AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`: **255/255 passed** (249 baseline plus six review regression cases).
- `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true --no-restore`: passed with **zero warnings/errors**.

November 8, 2026 review follow-up: six deterministic regression cases failed
against the original implementation before the fixes. Three cover final-read
expiry/exhaustion and verification latency followed by a supplied timer reset.
Three cover completion before any write, a different completed champion after an
own hover, and a matching own completion. They verify no later writes, actual
champion/action and conservative manual attribution in shared tray state,
diagnostics state and `draft.pick` history. Native acceptance remains pending.


November 8, 2026 ticket 05 verification: **285/285 tests passed** (255 baseline
plus 30 policy/persistence/diagnostic cases); Release Windows cross-build passed
with zero warnings/errors. Policy cases cover all choices, live countdown/final-read
changes, revision-gate rejection, in-flight requests, disarm, exhausted plans,
invalid policies and v1 reloads. Diagnostics `pick` and `rolePlans` include the
current `manualPickPolicy` and `policyExplanation`; role-plan evidence updates on
publication even without a draft. Native gates remain false.

Windows/League acceptance is unavailable on this Linux host. In addition to the
existing gates, verify keyboard navigation and high-DPI layout of all Settings
choices/explanations, immediate effective revision and save/failure/retry status,
closing without saving delay while retaining the policy, reopen/restart, and all
three live League behaviors including changes during countdown and final reads.
These native checks are pending; automated simulations are not native evidence.
