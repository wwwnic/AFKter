# lol-testbed — runtime evaluation

Evaluated November 8, 2026 at upstream commit
`16ebe72a77378e609d6a6bbbe4e8a79cccdf1ccb`, using Python 3.12.14 and Afkter
Core at `710735f`. The external source was cloned into the shared workspace and
its requirements installed in a separate virtual environment. No upstream or
Afkter production source was modified.

## Upstream suite and scenarios

The unmodified suite passed **21/21 tests** in 0.16 seconds. Pytest emitted a
configuration deprecation warning about the default async fixture loop scope;
there were no test failures. Passing these tests verifies the tool's own checks,
not Afkter compatibility.

The supplied queue scenario replayed five steps and ended in ChampSelect. Its
ready-check event contained InProgress, but the HTTP ready-check state remained
Invalid: generic replay steps broadcast events without updating that REST state.
The supplied draft scenario replayed two steps and loaded a session with ban and
pick action objects. It does not itself play a full evolving draft sequence.

## Actual Afkter connection and command results

The stock mock uses HTTP, and Afkter correctly rejected its HTTP lockfile. A small
external launcher added a loopback HTTPS listener with a temporary self-signed
certificate to the same original aiohttp application. This changed neither the
mock source nor Afkter's HTTPS-only transport. The results below distinguish
that launcher adaptation from unmodified upstream compatibility.

| Check | Observed result |
| --- | --- |
| Stock HTTP lockfile | Rejected by Afkter, as expected |
| WSS ready-check delivery through external TLS listener | Event received |
| That event passed to Afkter's actual decoder | Ignored: emitted topic is global `OnJsonApiEvent`; Afkter expects its subscribed resource-specific topic |
| Ready-check GET after queue replay | Invalid, despite replay's InProgress event |
| GET `/lol-gameflow/v1/session` | 404 |
| GET pickable/bannable champion IDs | Both 404 |
| Afkter's actual draft reader | Blocked by missing gameflow endpoint |
| Ready-check GET after explicitly seeding mock HTTP state | InProgress |
| Afkter accept POST | GET subsequently reports Accepted |
| Afkter ban PATCH | Accepted; action 1 becomes champion 122, completed |
| Afkter hover PATCH | Accepted; action 2 becomes champion 86, incomplete |
| Afkter lock PATCH | Accepted; action 2 becomes champion 86, completed |
| Mock history | One accept and three champ-select patches recorded |

These writes were direct calls through Afkter's real HTTP adapters. The script
explicitly seeded ready-check HTTP state for that part. It did not run Afkter's
normal automatic controllers through a complete queue-to-draft journey. The mock
also permits action updates without enforcing active-turn eligibility, so an
accepted PATCH is not proof of realistic draft rules or timing.

The source-level gaps in [LCU references](lcu-api.md#mock-client-and-scenario-replay)
remain: required availability/lifecycle endpoints and draft fields are missing.
The mock does not validate Authorization. It therefore cannot establish native
credential validation, process discovery, UI behavior or real-client compatibility.

## Practical assessment and reproduction

The tool runs readily on Linux and offers a small scenario/history API. It is
useful for experimentation and scenario ideas, but the tested version needs a
compatibility bridge before full Afkter integration. It does not currently save
that work compared with the existing JoinGameAfk mock, whose HTTPS server and
command handling were [already exercised](existing-mock-client-smoke.md).

The checkout, virtual environment, and smoke runner remain outside the app at
`/workspace/shared/lol-testbed`, `/workspace/shared/lol-testbed-venv`, and
`/workspace/shared/lol-testbed-smoke`. The runner directory contains its exact
commands, Python launcher, .NET project, output, and resolved dependency versions.
These are local shared-workspace artifacts; this report is the committed record.
The .NET smoke project built with zero warnings/errors. Certificates and mock
lockfiles were temporary, and both listeners were stopped after the run.

No application behavior, native draft-write gate, or ticket 07 completion claim
changed. Routine testing can remain lightweight while documenting these limits.
