# League Client API References

## Endpoint and Schema Lookup

Use the LCU explorer to look up HTTP methods, paths, request bodies, response types, and events for the local League of Legends client. Check the client version shown by the explorer and verify the routes and payloads your change relies on against the installed client. [LCU API explorer](https://lcu.kebs.dev/)

- Use the machine-readable schema for endpoint and type definitions. [LCU Swagger JSON](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json)
- Consult the separate Riot Client explorer for launcher endpoints. [Riot Client API explorer](https://riotclient.kebs.dev/)
- Follow the documentation generator and published schemas in the source repository. [KebsCS documentation source](https://github.com/KebsCS/lcu-and-riotclient-api)

## Implementation Reference

Use JoinGameAfk for examples of ready-check handling, gameflow monitoring, and champion-select automation. Its draft-pick notes describe the local LCU connection and map the routes used by its implementation. [JoinGameAfk repository](https://github.com/wwwnic/JoinGameAfk) · [Draft Pick Overview](https://github.com/wwwnic/JoinGameAfk/blob/674d0927303782e46d56a7efffd819147192358b/.github/draft-pick-overview.md)

## Mock Client and Scenario Replay

[lol-testbed](https://github.com/Intrusive-Thots/lol-testbed) is an external Python
LCU mock worth evaluating for lightweight development without a running League
client. It provides HTTP endpoints, WAMP WebSocket events, temporary lockfiles,
request history, and JSON scenario replay, including ready-check and draft
pick/ban examples. See its [README](https://github.com/Intrusive-Thots/lol-testbed/blob/master/README.md)
for setup and usage.

Source inspection on November 8, 2026 found that it is **not yet a drop-in
Afkter test environment**:

- Its [server](https://github.com/Intrusive-Thots/lol-testbed/blob/master/mock_lcu/server.py)
  uses HTTP; Afkter's production transport requires HTTPS/WSS.
- It does not implement `/lol-gameflow/v1/session`,
  `/lol-champ-select/v1/pickable-champion-ids`, or
  `/lol-champ-select/v1/bannable-champion-ids`.
- Its [draft sample](https://github.com/Intrusive-Thots/lol-testbed/blob/master/mock_lcu/scenarios/draft_5v5_pick_ban.json)
  omits fields Afkter requires, including teammate `championPickIntent` and timer
  `isInfinite`.

A subsequent [runtime evaluation](lol-testbed-evaluation.md) installed and ran
the tool: its 21 tests passed, and Afkter sent direct commands through an external
HTTPS listener. Full automation remains blocked by the gaps above plus event/REST
state and WebSocket-topic differences. It remains a candidate for future
integration and a source of scenario ideas; adapting it should
preserve Afkter's production identity, timing, and transport checks. Recheck the
upstream implementation before integration because these links track its default
branch.

For a mock already exercised here, see the
[JoinGameAfk Linux smoke results](existing-mock-client-smoke.md). Routine
development can use focused automated checks and mock scenarios, with occasional
real-client testing when the player uses the app. Record simulated and native
results separately.
