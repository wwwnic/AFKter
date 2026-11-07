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
- [ ] Document setup/build/test/run and manual League restart checks. Validate from
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
