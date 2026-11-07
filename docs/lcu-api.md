# League Client API References

## Endpoint and Schema Lookup

Use the LCU explorer to look up HTTP methods, paths, request bodies, response types, and events for the local League of Legends client. Check the client version shown by the explorer and verify the routes and payloads your change relies on against the installed client. [LCU API explorer](https://lcu.kebs.dev/)

- Use the machine-readable schema for endpoint and type definitions. [LCU Swagger JSON](https://raw.githubusercontent.com/KebsCS/lcu-and-riotclient-api/main/lcu/swagger.json)
- Consult the separate Riot Client explorer for launcher endpoints. [Riot Client API explorer](https://riotclient.kebs.dev/)
- Follow the documentation generator and published schemas in the source repository. [KebsCS documentation source](https://github.com/KebsCS/lcu-and-riotclient-api)

## Implementation Reference

Use JoinGameAfk for examples of ready-check handling, gameflow monitoring, and champion-select automation. Its draft-pick notes describe the local LCU connection and map the routes used by its implementation. [JoinGameAfk repository](https://github.com/wwwnic/JoinGameAfk) · [Draft Pick Overview](https://github.com/wwwnic/JoinGameAfk/blob/674d0927303782e46d56a7efffd819147192358b/.github/draft-pick-overview.md)
