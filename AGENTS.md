# Repository Guidelines

## Project Structure

Afkter is an initial Git repository. The first project contribution should add a root `README.md` that identifies the application entry point and the locations of source code, tests, and assets. Follow the chosen framework's directory conventions. Add a `.gitignore` for generated output, caches, and machine-specific settings when introducing tooling.

## League Client Reference

For League Client (LCU) endpoints, payloads, events, and implementation examples, see [League Client API references](docs/lcu-api.md).

## Build, Test, and Run

When adding the first runnable code, document the required toolchain and version, dependency setup, and exact commands to run locally, build, and test from the repository root. Run those commands from a fresh checkout before publishing them. Identify any required environment variables and use placeholders in checked-in examples.

## Coding Style and Naming

When introducing a language, record its indentation and naming conventions alongside the tooling setup. If the project adopts a formatter or linter, check in its configuration and document the command contributors should run. Match nearby code as conventions emerge and keep unrelated formatting changes separate.

## Testing

When adding a test runner, document the framework, test directory, test file naming pattern, and how to run a single test and the full suite. If the project adopts a coverage threshold, record it with the test command. For behavior changes, report the checks you ran and include reproduction steps for any manual verification.

## Commits and Pull Requests

Use short, imperative commit subjects, such as `Document local setup`. Keep each commit focused on one change. In pull requests, summarize what changed and why, list validation commands and results, link the related issue when available, and include screenshots for visible UI changes.

## Agent skills

### Issue tracker

For publishing, reading, or updating local HTML specs and tickets, see [Issue tracker](docs/agents/issue-tracker.md).

### Triage labels

When assigning triage status, use the [Triage labels](docs/agents/triage-labels.md).

### Domain docs

Before exploring a domain concept or revisiting a design decision, see [Domain docs](docs/agents/domain.md) for the single-context layout.
