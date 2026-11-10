# Repository Guidelines

## Project Structure

Afkter is a .NET 10 Windows tray companion. Current projects are `src/AFKtor.Core` (controllers, plans, transport, persistence and diagnostic contracts), `src/AFKtor.Diagnostics` (bounded history/read API), and `src/AFKtor.Tray` (WinForms composition/UI/discovery). Tests are in `tests/AFKtor.Tests`; bundled champion metadata is under Core's `Assets`. See `README.md` for the active entry point and architecture map. Avalonia migration projects are specified in the approved design and implementation plan; do not describe them as implemented until they exist. Keep generated output, caches and machine-specific settings ignored.

## League Client Reference

For League Client (LCU) endpoints, payloads, events, and implementation examples, see [League Client API references](docs/lcu-api.md).

## Build, Test, and Run

Keep the required toolchain/version, dependency setup, and exact local build/run/test commands current in README.md. Run those commands from a fresh checkout before publishing them. Identify any required environment variables and use placeholders in checked-in examples.

## Coding Style and Naming

When introducing a language, record its indentation and naming conventions alongside the tooling setup. If the project adopts a formatter or linter, check in its configuration and document the command contributors should run. Match nearby code as conventions emerge and keep unrelated formatting changes separate.

## Testing

The current NUnit suite uses `tests/AFKtor.Tests` with `*Tests.cs`; README.md documents focused and full-suite commands. Document equivalent commands for any new test project. If the project adopts a coverage threshold, record it with the test command. For behavior changes, report the checks you ran and include reproduction steps for any manual verification.

## Commits and Pull Requests

Use short, imperative commit subjects, such as `Document local setup`. Keep each commit focused on one change. In pull requests, summarize what changed and why, list validation commands and results, link the related issue when available, and include screenshots for visible UI changes.

## Agent skills

### Issue tracker

For publishing, reading, or updating local HTML specs and tickets, see [Issue tracker](docs/agents/issue-tracker.md).

### Triage labels

When assigning triage status, use the [Triage labels](docs/agents/triage-labels.md).

### Domain docs

Before exploring a domain concept or revisiting a design decision, see [Domain docs](docs/agents/domain.md) for the single-context layout.
