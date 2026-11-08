# Live timing — ticket 06

Main Settings and its initially collapsed advanced section publish valid edits
immediately, without Apply. Invalid/unfinished numeric input preserves the whole
effective revision and displays an error. Closing keeps valid edits. Restore
author timing defaults publishes at most one revision containing all five default
values; plans and the manual-pick deadline policy are preserved.

| Control | Author default | Supported range |
| --- | --- | --- |
| Ready-check delay | 3 seconds | 0–30 whole seconds |
| Normal action pacing | 1000 ms | 0–5000 ms |
| Hover-to-lock wait | 1000 ms | 0–5000 ms |
| Urgent threshold | 3000 ms | 1000–10000 ms |
| Request safety margin | 250 ms | 250–1000 ms, strictly below urgency |

These engineering bounds retain the original minimum request budget and provide
an urgent interval above that budget. Five-second normal waits limit accidental
long stalls; ten-second urgency limits how much normal pacing is bypassed.
They are supported configuration bounds, not measured installed-client latency.
Zero pacing/hover waits are intentional; expiry and the safety floor still apply.
Defaults require no customization. Urgent draft actions skip pacing/hover waits,
while safety always blocks requests at or below its margin.

Draft timing edits re-evaluate on the next serialized refresh, retaining the
original local-action and hover-request start times. Final verification uses the
latest timing revision and the same publication/dispatch gate as role plans and
policy. Pick and ban deadlines are monotonic and only shorten, including final
reads which cannot dispatch. Supplied timer resets and edits cannot revive an
expired action. Confirmed completed picks remain terminal even if a later read
regresses. Pending/sent writes retain serialization and cannot be recalled.
The existing two-second host polling cadence remains best effort.

Ready-check delay edits wake the single existing operation owner. The due time is
original detection plus the latest delay: a shorter elapsed delay verifies now;
a longer delay moves the undispatched due time. Every edit during a held final
read requires fresh verification before send, including when the new due time
elapses while the read is held. Compatibility checks still compare the original
check's initial timer plus actual elapsed local time at verification start, and
reject backwards timers using that operation's own session evidence. Duplicate
events, skipped initial/disarmed checks and failed/finished attempts never acquire
a new owner. Startup stays disarmed and sent accepts are never retried.

Timing lives in the complete immutable role-plan revision and shares its serial
500 ms autosave, atomic flushed temporary-file replacement, saved revision,
UNSAVED failure indication and automatic/manual retry. Restore defaults is atomic
in memory and on disk. A blocked/older save cannot mark a newer edit saved.
Failure preserves current effective settings; restart recovers the last saved
revision. The role-plan editor and main Settings report shared save health.
Diagnostics `rolePlans.timing` exposes all five effective values; the controller
reports live due/remaining time under its existing operation identity.

Storage remains the legacy technical directory `%LOCALAPPDATA%\AFKtor`.
Schema-version-1 role plans accept an optional `Timing` member. Missing timing
preserves old role priorities and policy, using existing `settings.json` delay
(or 3 for missing/malformed/out-of-range legacy data) with draft defaults.
This import is read again until a subsequent valid edit saves the complete
revision; the original legacy file stays untouched. Thereafter stored timing
wins over legacy delay. Explicit null, missing members, malformed numbers,
out-of-range values and unsafe combinations reject a complete new revision,
visibly recover safe defaults/Do nothing with empty plans, and preserve the
original file until a valid edit. Old v1 manual-policy compatibility is unchanged.

## Verification and limits

.NET SDK 10.0.403 deterministic tests cover live shortening/lengthening, held
verification edits including elapsed replacement due times, timer identity,
zero/maximum ready delay, zero/maximum draft waits, dispatch boundaries, original
clocks, monotonic expiry, terminal completion, invalid input, coherent reset,
legacy migration, malformed files, save failures and retry. Existing whole-plan
blocked-save coverage applies to timing through the same writer.

November 8, 2026 validation: baseline **285/285**; final Release suite
**304/304 passed**. Release Windows cross-build passed with **zero warnings and
errors**; `git diff --check` passed.

November 8, 2026 review follow-up: final pick verification now records a completed
same-session local action before requiring an active action. Its actual champion,
action and conservative manual attribution remain terminal in tray state,
diagnostics state and history even after stale incomplete polls and timing,
plan or policy edits. Three held-read regressions first failed with `Cancelled`
instead of `Completed`; all now pass. Five additional guards reject unrelated
game/action/local-cell/role/queue completion evidence without poisoning the current
action. Release suite: **312/312 passed**; Release Windows cross-build:
**zero warnings/errors**; `git diff --check` passed. Native gates remain false.

Windows and installed League are unavailable on this Linux host. The shipped
pick and ban `nativeSemanticsVerified` gates remain **false**. Native checks are
pending: Settings keyboard/high-DPI layout, collapsed advanced controls, all
normal/urgent boundaries, editing during countdown/final reads/in-flight writes,
reset and failure/retry/restart, migration, tray/diagnostics agreement and real
League timing. Cross-compilation and simulations are not native acceptance.
Ticket 07 has not started.
