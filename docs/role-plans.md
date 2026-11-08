# Role plans

Open **Role plans…** from the tray menu. Select Top, Jungle, Middle, Bottom, or
Support, search by champion name or numeric League champion ID, then add to the
pick or ban priority list. Select an entry and use Up/Down to reorder or Remove
to delete it. Each button completes exactly one edit; selecting a role/champion,
searching, duplicate additions, and invalid moves do not publish revisions.
There is no drag operation or Apply button. Empty lists mean no planned action.

`RolePlanService.Current` supplies a complete immutable revision of role plans and
the manual-pick deadline policy configured in main Settings. Changing that policy
is one complete live edit, sharing the same publication, autosave and dispatch
guard as plan changes. It does not clear manual takeover. The policy defaults to
Lock current hover; Use my role plan and Do nothing are also available.

The service supplies a complete immutable revision to future draft
consumers. A valid edit replaces the whole revision immediately. Saving runs
separately, serially, every 500 ms, coalescing rapid edits to a coherent snapshot.
A successful older save cannot mark a newer edit saved. Same-directory temporary
files are flushed before replacement; the previous file remains intact until
replacement. Shutdown attempts a final save. Unsaved changes can still be lost
if the process exits while storage is unavailable; the UI never promises otherwise.

Storage is `%LOCALAPPDATA%\AFKtor\role-plans.json`, independent of accept-delay
settings. The editor shows the effective revision, saved revision, pending save,
and save failure. Failed writes keep the latest revision effective and retry
automatically; Retry saving requests an immediate retry. On restart the last
successful complete revision is recovered. Invalid schema, missing/duplicate roles,
unknown IDs, duplicate priorities, invalid revision numbers, and malformed/oversized
files are rejected as a whole. Recovery starts with empty plans, a safe Do nothing policy and a visible
warning; the original file is preserved until the player makes a valid edit.
Diagnostics `/v1/state` adds `rolePlans` (revision, savedRevision, health, recovery);
`rolePlans.changed` records the same fixed-shape evidence in existing event history.
No raw stored content or exception messages enter diagnostics.

## Champion identification and updates

The bundled `src/AFKtor.Core/Assets/champion.json` is Riot's official Data Dragon
English champion identity catalog, version **16.22.1**, retrieved November 8, 2026:

- [Official champion catalog](https://ddragon.leagueoflegends.com/cdn/16.22.1/data/en_US/champion.json)
- [Official version index](https://ddragon.leagueoflegends.com/api/versions.json)

Names identify champions to the player; numeric `key` values are League champion
IDs. The editor uses no external images or artwork. A bundled snapshot makes all
editor and recovery flows work without League or an internet connection. LCU
catalog endpoints require a running authenticated League client, so this editor
does not depend on them. The catalog supplies identity only, never live ownership,
pick/ban eligibility, or a fallback champion.

Catalog updates ship with Afkter releases: retrieve the current official version
and replace the snapshot, then run catalog and persistence tests. The app performs
no network refresh. A new champion absent from the bundled snapshot cannot be
added until an update ships. Older snapshots remain usable when offline. If the
embedded catalog is missing or malformed, the app shows catalog unavailable,
disables edits, keeps effective plans empty, and preserves the stored file. It
never guesses names/IDs or automatically substitutes champions.

## Verification and remaining native gates

Linux NUnit tests exercise real temporary files, all five roles, empty plans,
ordered immutable edits, duplicate/unknown rejection, 505 rapid complete edits,
background autosave, blocked-save interleaving, real storage failure, automatic
retry, restart recovery, malformed data, bundled identities, and diagnostics.
The WinForms host is cross-compiled with the solution Release build.

Native Windows execution is unavailable in this environment. Before declaring
native acceptance complete, on Windows without League:

1. Open/reopen the modeless editor, navigate by keyboard, search champions,
   edit both lists for all roles, reorder rapidly, and remove every entry.
   Check sizing at normal and high DPI and ensure the tray stays responsive.
2. Watch the effective revision change once per complete operation, with no
   changes from search/selection/duplicate additions. Check saved status follows.
3. Make the storage destination unwritable, edit, and confirm the latest plan
   remains displayed with UNSAVED failure. Restore access and verify both automatic
   retry and the Retry saving button, then restart the app and check recovery.
4. Close/reopen the editor and restart the application. Verify the last saved
   priorities and revision persist. Corrupt a stored file and confirm the warning,
   empty effective plans, and preservation of that file until a new edit.
5. Query local authenticated diagnostics and confirm revision/save health agree
   with the UI through successful writes and failures.

These UI steps are pending; cross-compilation and simulations do not establish
Windows visual behavior or live League readiness. Draft action execution belongs
to later tickets.


The existing schema-version-1 file gains an optional numeric `ManualPickPolicy`
field. Valid old v1 files without it preserve every role/priority and default to
Lock current hover. New saves retain schema 1 and write the policy with the same
whole revision. Explicit null, unknown numeric policy or malformed policy values
reject the complete revision, preserve the original file and visibly recover to
Do nothing with empty plans. Failed policy writes keep the latest effective policy
and retry using the same serialized writer; an older save cannot mark it saved.
Diagnostics role-plan evidence includes the policy and its player explanation.
