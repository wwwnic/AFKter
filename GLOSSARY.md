# Afkter

Afkter is a companion for the League of Legends queue.

## Language

**Ready check**  
The League Client prompt asking a player to accept a match the queue has found.

**Auto-accept**  
Afkter's opt-in action that accepts a ready check on the player's behalf.

**Accept attempt**:
A single request to accept a ready check. Its successful completion does not by itself confirm that League accepted the match.

**Armed**  
The state in which the player has enabled auto-accept for the current Afkter session. A League Client restart preserves this state, and a new Afkter session starts disarmed.

**Draft**
The champion-select lifecycle identified by League's game ID, containing planning,
ban, pick and finalization phases. Afkter currently observes normal draft pick only.

**Draft admission**
Evidence that Afkter observed a new supported draft while enabled on an uninterrupted
connection. Admission describes lifecycle eligibility; the read-only observer sends
no draft actions. Enabling or reconnecting during a draft does not admit that draft.

**Assigned role**
The local player's position supplied by League: top, jungle, middle, bottom or utility.
An absent or unknown value is never guessed.

**Role plan**

The player's ordered pick and ban priorities for one assigned draft role: Top,
Jungle, Middle, Bottom, or Support. An empty list means no planned action.

**Plan revision**

An immutable snapshot of all role plans, timing settings and the manual-pick deadline policy
published after one complete valid edit.
The effective in-memory revision can be newer than the last saved revision.


**Manual-pick deadline policy**

The player's live setting for automatic pick behavior near expiry after a manual
hover change: Lock current hover (default), Use my role plan, or Do nothing.
Changing it never clears manual control for the current draft.


**Champion catalog**

The browseable, searchable list of champions from which the player builds role
plans. Its role filter narrows the displayed champions; it does not change the
player’s assigned role or the saved plans.
