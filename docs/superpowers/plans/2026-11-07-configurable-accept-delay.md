# Configurable Accept Delay Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Complete ticket 03: persist the chosen delay, show the countdown, and keep each active check's original deadline.

**Architecture:** Extend the existing .NET 10 WinForms tray application. A small settings window edits the delay; a Core settings store persists only that value. The controller captures the configured delay when it schedules each eligible check and uses that captured duration for final timer verification.

**Tech Stack:** C#, .NET SDK 10.0.400, WinForms, System.Text.Json, NUnit 4.

**Spec:** [AFKtor v1](../../../.scratch/ready-check-auto-accept/spec.html)

**Ticket:** [03 — Configure the accept delay and see the countdown](../../../.scratch/ready-check-auto-accept/issues/03-configurable-accept-delay.html)

## Approved design

Use the spec's small settings window with a numeric delay field, Save, and Cancel. Reuse the existing tray countdown. Approved implementation choices: whole seconds from 0 through 30; store JSON at `%LOCALAPPDATA%/AFKtor/settings.json`; default to 3 seconds for missing, malformed, or invalid settings. Zero means immediate verification before acceptance. Longer delays may outlast a ready check, which must still cancel normally.

A fixed-value tray submenu is simpler but restricts choices; a configuration-file-only interface does not provide the requested native settings window. The proposed numeric window fits the existing WinForms application without new dependencies.

Save the file successfully before changing the live setting. If saving fails, show an actionable error and retain the previous value. Cancel changes nothing. Never persist arming or LCU credentials. Settings affect future checks only. Opening settings must leave tray timers and cancellation running; own and dispose the settings window on shutdown.

## Global constraints

- Default to a 3-second delay and save the delay locally between launches.
- Changes apply to the next ready check; an active deadline stays unchanged.
- Every AFKtor launch starts disarmed.
- Preserve the existing once-per-check, cancellation, reconnect, and final-verification rules.
- Keep warnings as errors and add no package dependencies.
- Live Windows/League validation remains required; Linux compilation cannot verify native interactions.

## Review focus

- Missing, corrupt, out-of-range, and unreadable settings must not crash startup.
- A failed write must not silently change the effective setting or destroy the previous file.
- An edit during a countdown must not change its deadline or its final timer threshold.
- Countdown state must clear before final GET/POST, including a slow request.
- Zero-delay checks must still obey arming, lifecycle, duplicate-event, and final-verification checks.

## Task 1: Delay settings and persistence

**Files:** Create `src/AFKtor.Core/AcceptDelaySettings.cs`, `src/AFKtor.Core/AcceptDelaySettingsStore.cs`, and `tests/AFKtor.Tests/AcceptDelaySettingsTests.cs`.

**Interfaces:** `AcceptDelaySettings(int DelaySeconds)` validates the approved 0–30 integer range and exposes `Default` (3 seconds). `AcceptDelaySettingsStore(string path)` exposes `Load()` and `Save(AcceptDelaySettings settings)`; startup load failures return the default, save failures propagate for UI reporting.

- [x] Write tests for default 3, boundaries 0/30, invalid values, round-trip across store instances, missing/malformed/unreadable files, and preservation of the previous file when saving fails. Use isolated temporary directories and clean them up.
- [x] Run `dotnet test tests/AFKtor.Tests --filter FullyQualifiedName~AcceptDelaySettings` and confirm failure for the missing behavior.
- [x] Implement validated settings and JSON persistence. Write a unique temporary file beside the destination, replace only after successful serialization/write, and clean up temporary files. Save only the delay. Do not silently swallow save errors.
- [x] Rerun focused tests and commit the passing deliverable.

## Task 2: Capture the delay per check and clear the countdown

**Files:** Modify `src/AFKtor.Core/ReadyCheckController.cs` and `tests/AFKtor.Tests/ReadyCheckControllerTests.cs`.

**Interfaces:** Add a lock-protected `TimeSpan AcceptDelay` property, defaulting to 3 seconds and using the settings range. Extend `PendingAccept` with its captured delay. Keep existing constructor and session interfaces compatible. `Remaining` describes only the waiting phase, not final verification or a request in flight.

- [x] Add deterministic clock tests for configured 0/1/5/30-second delays and no early acceptance. Supply compatible elapsed timers in fake final responses.
- [x] Add a test that schedules at 3 seconds, changes the setting to 5 after 1 second, observes acceptance at the original 3-second deadline, and sees the next check wait 5 seconds. Assert duplicate events preserve the deadline.
- [x] Add held-GET and held-POST tests asserting `Remaining` is null once waiting ends. Extend cancellation tests to assert countdown clearing on disarm, decline, expiration, disconnect, and shutdown.
- [x] Add configurable-delay timer-compatibility and zero-delay duplicate/final-validation tests. Confirm these new tests fail before editing production code.
- [x] Replace all scheduling-related hardcoded three-second values with the captured duration. Compute the final minimum elapsed time using the captured duration plus actual scheduling lateness. Clear countdown visibility when verification starts without discarding operation identity or cancellation ownership.
- [x] Run controller tests and the complete Core test suite. Commit when passing.

## Task 3: WinForms settings and handoff

**Files:** Create `src/AFKtor.Tray/SettingsForm.cs`; modify `src/AFKtor.Tray/TrayApplicationContext.cs`, `README.md`, and ticket 03's HTML comments.

**Interfaces:** A single owned settings window exposes the numeric delay and Save/Cancel. The tray owns the settings store and applies successful saves through `ReadyCheckController.AcceptDelay`. The tray's existing display timer renders the controller's countdown.

- [x] Load settings before starting session discovery; initialize the controller delay without arming it.
- [x] Add a Settings tray command. Reopening focuses the existing window. Show the effective saved value, a seconds label, and a short note that edits apply to the next ready check. Save persists then applies the delay; Cancel and closing discard edits. Show save errors without closing or changing the effective value.
- [x] Ensure settings disposal follows tray shutdown and that rendering/network discovery continue while the window is open.
- [x] Update README with the range, storage path, fallback/error behavior, and exact manual checks: first-launch default, save/restart persistence while disarmed, edit during countdown, next-check delay, countdown clearing, and settings-window shutdown.
- [x] Run `dotnet test AFKtor.slnx -p:EnableWindowsTargeting=true`, `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`, and `git diff --check`. Require zero test failures and zero build warnings/errors.
- [x] Review the complete diff against ticket 03 and each review-focus condition. Record test results and outstanding Windows/League checks in the ticket comments; commit the deliverable.

## Execution

Approved 2026-11-07; implement inline in this chat. Branch: `codex/ticket-3-configurable-delay`, based on ticket 02 commit `886175c`. Do not report live acceptance checks complete until performed with Windows and League.

## Verification record

Implemented in `764ff01`, `b4a1b0c`, and `7e9f014`. All 95 tests passed and the Release Windows cross-build reported zero warnings/errors. Independent read-only whole-branch review found no defects. Native WinForms, Windows filesystem, and installed League checks remain pending, including the inherited conservative timer policy at zero delay. The task used its existing cloud checkout and feature branch; no additional worktree was created. No review findings were deferred.
