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
