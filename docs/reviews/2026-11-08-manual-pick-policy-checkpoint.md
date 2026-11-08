# Manual pick policy — ticket 05 checkpoint

The player authorized ticket 05 using implement-spec. Settings now offers three
explained choices after a manual pick change: **Lock current hover** (default),
**Use my role plan**, and **Do nothing**. Changes apply immediately and autosave
through the existing immutable revision and serial persistence mechanism.

Current-hover policy never substitutes for an invalid hover. Role-plan policy
selects and locks the first eligible champion from the latest assigned-role plan,
even when replacing a manual hover; exhaustion produces evidence and no write.
Do nothing suppresses selection and watchdog work after takeover. Policy edits do
not clear manual takeover or extend deadlines. The shared revision gate prevents
superseded queued work dispatch; already-sent requests remain irrevocable and must
reconcile. Disarm cancels all policies.

Legacy stored plans without a policy retain the default. Invalid policy data
recovers visibly to Do nothing. Save failures retain the effective policy and
support retry. Settings distinguishes immediate policy autosave from its existing
delay-only Save/Cancel contract; ticket 06 has not changed ready-check timing.

Implementation `d3f9753` was integrated as merge `e056f6f` on
`feature/draft-companion`. A fresh read-only reviewer inspected the full change
against the approved spec, ticket, glossary and diagnostic ADR, and found no
material defect. Native UI/League checks and later timing work were explicitly
left outside the review's validation claims.

Integration verification with SDK 10.0.403 ran:

```sh
dotnet test AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true
git diff --check 9e2f1ac..HEAD
```

All **285 tests passed**, none failed or skipped, including 30 new policy,
persistence, diagnostic and interleaving cases. The Release Windows cross-build
had **zero warnings and errors**. Whitespace validation passed.

Both shipped draft-write gates remain false. Windows layout, keyboard/DPI,
filesystem/UI behavior and live League acceptance remain pending on this Linux
host; follow [planned-pick checks](../planned-picks.md) and
[role-plan persistence checks](../role-plans.md). Work pauses after commit and
push. Tickets 06 and 07 remain untouched.
