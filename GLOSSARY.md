# AFKtor

AFKtor is a companion for the League of Legends queue.

## Language

**Ready check**  
The League Client prompt asking a player to accept a match the queue has found.

**Auto-accept**  
AFKtor's opt-in action that accepts a ready check on the player's behalf.

**Accept attempt**:
A single request to accept a ready check. Its successful completion does not by itself confirm that League accepted the match.

**Armed**  
The state in which the player has enabled auto-accept for the current AFKtor session. A League Client restart preserves this state, and a new AFKtor session starts disarmed.

**Draft**
The champion-select lifecycle identified by League's game ID, containing planning,
ban, pick and finalization phases. AFKtor currently observes normal draft pick only.

**Draft admission**
Evidence that AFKtor observed a new supported draft while enabled on an uninterrupted
connection. Admission describes lifecycle eligibility; the read-only observer sends
no draft actions. Enabling or reconnecting during a draft does not admit that draft.

**Assigned role**
The local player's position supplied by League: top, jungle, middle, bottom or utility.
An absent or unknown value is never guessed.
