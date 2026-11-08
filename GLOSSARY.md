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

**Role plan**

The player's ordered pick and ban priorities for one assigned draft role: Top,
Jungle, Middle, Bottom, or Support. An empty list means no planned action.

**Plan revision**

An immutable snapshot of all role plans published after one complete valid edit.
The effective in-memory revision can be newer than the last saved revision.
