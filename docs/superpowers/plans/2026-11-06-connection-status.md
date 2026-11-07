# League connection status implementation plan

**Goal:** Implement ticket 01, Show League Client connection status in the tray.
**Spec:** `.scratch/ready-check-auto-accept/issues/01-league-connection-status.html`
**Architecture:** C#/.NET 10 WinForms tray, a UI-independent connection monitor,
and a local HTTPS probe using credentials from the running client's lockfile.
Poll every two seconds; only a successful authenticated LCU response means connected.
Discover the executable location each time so custom installs and restarts work.

## Steps

- [x] Test and implement monitor state transitions through `RefreshAsync` and `IsConnected`.
- [x] Test and implement lockfile parsing and authenticated HTTPS probing, including
  malformed credentials, failed authentication, timeout, and replacement credentials.
- [x] Add process discovery and WinForms `ApplicationContext`, status tooltip/menu,
  non-overlapping polling, cancellation, and Exit cleanup.
- [x] Document setup/build/test/run and manual League restart checks. Validate from
  a fresh export, review standards and ticket requirements, and commit on current branch.

## Review focus

Client shutdown during discovery; partially written lockfiles; rotated ports/passwords;
unresponsive LCU; exit during a pending request. Credentials must never be logged,
redirected or sent through a proxy. The self-signed TLS exception is scoped to the
LCU loopback client. No auto-accept behavior belongs to ticket 01.

## Execution notes

The existing spec supplies the platform and architecture. Implementation is authorized
by the implement request; keep the current branch. This repository has no initial
commit, so review the added files against the empty tree. Existing user documents
remain untouched except for an appended ticket progress comment.

## Validation — 2026-11-06

- Observed failing tests before implementing connection transitions, credential
  rotation, and network/file failure recovery; focused tests then passed.
- Full suite: 14 passed. Debug and Release builds: zero warnings/errors.
- Fresh local checkout of `6a24d2a`: README restore, build, full-suite,
  single-file, single-test and Release commands all passed. The exact run command
  started a tray process that remained alive for four seconds, then was stopped.
- Validation used the documented restricted-environment variables and approved
  unsandboxed build/test execution because the sandbox prevented test-host IPC.
- Standards review: async test naming convention clarified in README. Optional
  duplicated lockfile parsing deferred: discovery checks PID ownership; the probe
  independently validates credentials. No blocking findings.
- Spec review: no code defects or scope creep. Live acceptance is unverified.
- Desktop automation denied Explorer access, preventing tray inspection/screenshots.
  League was not running. The README contains live startup/exit/restart checks;
  process smoke tests do not establish UI behavior or clean tray-menu Exit.
- Existing untracked project guidance/specs remain outside the implementation
  commits; the local ticket has an appended implementation/validation comment.
