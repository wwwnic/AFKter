# Avalonia Champion Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Afkter's utilitarian plan editor and WinForms shell with a simple Avalonia application while preserving existing automation, settings, saved plans and tray behavior.

**Architecture:** Core remains authoritative for validated edits, immutable revisions and autosave. A shared Avalonia presentation library supplies real views to an isolated Linux preview and the Windows production shell; a UI-independent runtime owns monitors and transport independently of windows. No separate editor process or duplicate settings writer.

**Tech Stack:** .NET 10 / repository SDK 10.0.400 with allowed patch roll-forward; C#; Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent and Avalonia.Headless.NUnit pinned to 12.1.5; NUnit 4.5.1 in the new presentation tests (required by the pinned headless package); existing Core tests retain their current NUnit version. Use small view models with INotifyPropertyChanged/ObservableCollection; no additional MVVM framework is required.

**Spec:** [Approved champion-editor design](../../../.scratch/plan-editor-redesign/spec.html).

**Layout refinement:** [Player-authorized GPT-6-ASTRA recommendations](../../research/2026-11-08-astra-layout-review.md).

**Tracker:** [Proposed task graph](../../../.scratch/plan-editor-redesign/ticket-breakdown.html). The design is approved; this implementation plan and its draft tickets await review. No production implementation has started.

## Global Constraints

- Use .NET 10, a pinned stable Avalonia release, standard controls/templates and a small MVVM presentation layer.
- Names remain visible. An empty search shows champions immediately; typing narrows the same list.
- One optional catalog filter: All, Top, Jungle, Middle, Bottom, Support. Champions may belong to multiple roles.
- Drag within one list to reorder. Drag between lists to copy and preserve the source; removal is explicit.
- If already present, highlight the existing entry instead of duplicating it.
- Publish one complete immutable edit on valid drop/command, never on drag-over or unfinished movement.
- Preserve startup as tray-resident and disarmed. Closing the editor window hides it and leaves monitoring/arming unchanged.
- Preserve legacy AFKtor technical/storage identities, role-plans.json schema/data and settings.json timing migration.
- Existing shipped draft-write verification gates remain unchanged by the UI migration.
- No Apply/Save button is introduced; effective edits survive save failure and remain visibly unsaved until recovery.
- No portrait downloads, browser runtime, JavaScript toolchain, paid UI component, new automation policy or installer/updater project.
- Run real shared views on Linux using isolated data and no League discovery/automation. Report missing Windows checks honestly without making them a prerequisite for every UI iteration.

## Review Focus

- Search owns Delete/arrow input: editing text must never remove/reorder a plan (Task 03 input test).
- A drag crosses a revision change: resolve stable IDs, reject stale anchors, and publish at most one current edit (Tasks 01/03 tests).
- Saving fails while the window changes page or hides: effective plans remain visible and Retry saves the newest revision (Tasks 02/05 tests).
- A window closes during a held network read: monitoring survives Hide, while explicit Exit cancels/drains exactly once (Tasks 04/05 tests).
- Role metadata is unavailable or a champion is new: All/search remain usable without silently deleting saved priorities (Tasks 01/02 tests).

## File Boundaries and Dependency Graph

| Task | Main files/responsibility | Blockers |
|---|---|---|
| 01 | Core stable-ID editing, bundled role metadata, domain tests | None |
| 02 | Avalonia presentation and isolated executable with click/keyboard editing | 01 |
| 03 | Shared drag interaction, insertion/overflow/focus behavior, control tests | 02 |
| 04 | UI-independent runtime extracted while current WinForms host still works | None |
| 05 | Production Avalonia shell/tray/Settings, migration and integration checks | 03, 04 |

Tasks 01 and 04 may run in parallel. Keep new projects out of unrelated refactors; additive solution edits may need merging. Each task gets a focused commit and fresh review using the established ticket/worktree workflow. A new task starts only within the player's authorized batch.

---

## Task 01: Stable plan edits and offline role filtering

**Files:** Create `src/AFKtor.Core/PlanEditCommand.cs`, `ChampionRoleCatalog.cs`, `Assets/champion-roles.json`, `THIRD-PARTY-NOTICES.md`, `tests/AFKtor.Tests/PlanEditCommandTests.cs`, `ChampionRoleCatalogTests.cs`; modify `RolePlanService.cs`, `RolePlanEditor.cs`, `AFKtor.Core.csproj` in the existing Core directory.

**Interfaces:**
- Existing inputs: `RolePlanService.Current`, `ChampionCatalog.LoadBundled()`, `DraftRole`, `PlanList`.
- Produce `enum PlanEditKind { Insert, Reorder, Remove }`, `enum PlanEditOutcome { Changed, Unchanged, Duplicate, Stale, Invalid }`.
- Produce `record PlanEditCommand(PlanEditKind Kind, DraftRole Role, PlanList List, int ChampionId, int? BeforeChampionId = null)` and `record PlanEditResult(PlanEditOutcome Outcome, int ChampionId, int? Index)`.
- Produce `RolePlanEditor.Apply(PlanEditCommand command): PlanEditResult`, backed by `RolePlanService.ApplyPlanEdit(PlanEditCommand command): PlanEditResult` under its existing revision lock.
- Produce `ChampionRoleCatalog.LoadBundled(ChampionCatalog identities): ChampionRoleCatalog`, `Available: bool`, `For(int championId): IReadOnlySet<DraftRole>`. Expose a `Parse(string json, ChampionCatalog identities)` factory for data-validation tests.

- [ ] **Step 1: Add failing domain tests.** Each fixture uses a real RolePlanService with a fresh temporary store, bundled identities and `startWorker:false`; seed with existing Replace, then construct RolePlanEditor. Pin source preservation and duplicate no-op:

```csharp
var before = plans.Current.Revision;
var result = editor.Apply(new(PlanEditKind.Insert, DraftRole.Jungle, PlanList.Picks, 86));
Assert.That(result.Outcome, Is.EqualTo(PlanEditOutcome.Changed));
Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.EqualTo(new[] { 86, 24 }));
Assert.That(plans.Current.For(DraftRole.Jungle).Picks, Is.EqualTo(new[] { 86 }));
Assert.That(plans.Current.Revision, Is.EqualTo(before + 1));
var revision = plans.Current.Revision;
Assert.That(editor.Apply(new(PlanEditKind.Insert, DraftRole.Jungle, PlanList.Picks, 86)).Outcome,
    Is.EqualTo(PlanEditOutcome.Duplicate));
Assert.That(plans.Current.Revision, Is.EqualTo(revision));
```

Also add named cases `ReorderUsesCurrentChampionIdentity`, `MissingSourceOrBeforeAnchorIsStaleWithoutRevision`, `SelfAndUnchangedDropAreNoOps`, `UnknownChampionIsInvalid`, `UnrelatedTimingAndPolicyEditsArePreserved`. Assert actual lists and exact revision deltas.
- [ ] **Step 2: Run** `dotnet test tests/AFKtor.Tests -c Release --filter 'FullyQualifiedName~PlanEditCommandTests|FullyQualifiedName~ChampionRoleCatalogTests'`; observe missing APIs/behavior fail before implementation.
- [ ] **Step 3: Implement the interfaces.** Null anchor means append; otherwise insert immediately before that current ID. Missing non-null anchor/source returns Stale. Reorder before itself is Unchanged. Insert into an existing destination returns Duplicate with its existing index, regardless of anchor. Remove affects only the named list. Validate and publish once using the existing gate/diagnostics/health path. Retain old index-based editor methods for the WinForms host until Task 05.
- [ ] **Step 4: Export role mapping** from JoinGameAfk commit `674d092`, `JoinGameAfk.Common/Model/ChampionCatalog.cs` or its equivalent bundled identity asset. Store a versioned JSON array of numeric `championId` plus `roles` enum strings; translate Mid→Middle and Adc→Bottom. Preserve the MIT notice. Data tests assert all 173 current identity IDs are covered, 281 memberships, Tryndamere 23 includes Top, Lux 99 includes Middle/Support. Invalid/missing role data sets Available=false; an unmapped identity returns an empty set and never affects plan loading. Class tags are not roles.
- [ ] **Step 5: Verify** the focused tests, then existing Core suite; require zero failures, including persistence/timing compatibility. Commit `Add stable plan edits and offline champion roles` with only this task's files.

## Task 02: Real Avalonia workspace and isolated preview

**Files:** Create `src/AFKtor.Presentation/AFKtor.Presentation.csproj`, `ViewModels/PlanEntryKey.cs`, `Styles/EditorTheme.axaml`, `Views/ChampionPlansView.axaml` and `.axaml.cs`, `ViewModels/ChampionPlansViewModel.cs`, `RolePlanRowViewModel.cs`, `PlanEntryViewModel.cs`, `src/AFKtor.Preview/{AFKtor.Preview.csproj,Program.cs,App.axaml,App.axaml.cs}`, `tests/AFKtor.Presentation.Tests/{AFKtor.Presentation.Tests.csproj,TestApp.cs,ChampionPlansViewModelTests.cs,ChampionPlansViewTests.cs}`; modify `AFKtor.slnx`, `README.md`.

**Interfaces:**
- Consume Task 01 types and the existing shared RolePlanService, never a second independent writer.
- Produce `record PlanEntryKey(DraftRole Role, PlanList List, int ChampionId)` in Presentation. Each `PlanEntryViewModel.Key` is a complete occurrence identity; catalog selection and insertion destination are separate.
- Produce `ChampionPlansViewModel(RolePlanService plans, ChampionCatalog identities, ChampionRoleCatalog roles)` with `SearchText: string`, `CatalogRole: DraftRole?` (null=All), `TargetRole: DraftRole`, `TargetList: PlanList`, `SelectedEntry: PlanEntryKey?`, `VisibleChampions: IReadOnlyList<Champion>`, five observable `RoleRows`, `TargetSummary: string`, `SelectionSummary: string`, `ActionFeedback: string`, and `SaveStatus: string`. Keep transient feedback independent of persistent save state.
- Produce `SelectTarget(DraftRole role, PlanList list): void`, `SelectEntry(PlanEntryKey key): void`, `Apply(PlanEditCommand command): PlanEditResult`, `AddChampion(int championId): PlanEditResult`, `MoveSelected(int delta): PlanEditResult`, `RemoveSelected(): PlanEditResult`, `Refresh(): void`, `RetrySaveAsync(): Task`. Selection/reveal follows the result; Duplicate selects the full destination entry key without mutation. SelectTarget changes only insertion destination and never rebinds the selected entry. Toolbar commands use SelectedEntry; actual plan-entry keyboard handlers derive their full key from the focused entry, never TargetRole/TargetList. Refresh clears an entry that disappeared rather than binding another occurrence of the same champion.
- `ChampionPlansView` binds this view model and names its search control `SearchBox`, role filter `CatalogRoleFilter`, destination summary `TargetSummary`, persistent save area `SaveStatus`, and transient area `ActionFeedback`. Add commands, selected-entry buttons and accessible names. Presentation refresh runs on the UI dispatcher every 250 ms and reconciles by full entry keys rather than replacing focused entries. Create one refresh owner per window, not per navigation/show event: start only while visible, stop while hidden/closing, resume the same owner on reopen, and dispose once. Settings and preview use this ownership contract too; no unowned repeated timers/subscriptions.

- [ ] **Step 1: Write failing view-model/control tests** using Avalonia.Headless.NUnit 12.1.5 with NUnit 4.5.1, Microsoft.NET.Test.Sdk 17.10.0 and NUnit3TestAdapter 4.6.0. Keep the existing Core test project unchanged. Construct fixtures with isolated stores. Pin discovery and independence:

```csharp
vm.CatalogRole = DraftRole.Top;
vm.SearchText = "trynd";
Assert.That(vm.VisibleChampions.Select(c => c.Id), Does.Contain(23));
vm.SelectTarget(DraftRole.Support, PlanList.Bans);
Assert.That(vm.CatalogRole, Is.EqualTo(DraftRole.Top));
Assert.That(vm.SearchText, Is.EqualTo("trynd"));
vm.AddChampion(23);
Assert.That(plans.Current.For(DraftRole.Support).Bans, Is.EqualTo(new[] { 23 }));
Assert.That(vm.SelectedEntry, Is.EqualTo(new PlanEntryKey(DraftRole.Support, PlanList.Bans, 23)));
```

Add a same-champion/two-lane fixture and pin subject independence:

```csharp
vm.SelectEntry(new(DraftRole.Top, PlanList.Picks, 86));
vm.SelectTarget(DraftRole.Jungle, PlanList.Bans);
vm.RemoveSelected();
Assert.That(plans.Current.For(DraftRole.Top).Picks, Does.Not.Contain(86));
Assert.That(plans.Current.For(DraftRole.Jungle).Bans, Does.Contain(86));
```

Add `SelectionKeyIncludesRoleAndList`, `MissingSelectionDoesNotRebindToAnotherOccurrence`, `RepeatedShowHideUsesOneRefreshOwner`, `BlankSearchShowsAlphabeticalCatalog`, `MissingRolesRetainsAllSearchAndPlans`, `DuplicateRevealsExistingEntry`, `FilterAndDestinationHaveDistinctAccessibleLabels`, `ActionFeedbackDoesNotOverwriteSaveFailure`, `RefreshPreservesSelectionAndText`, `FailedSaveKeepsEffectivePlanVisibleAndRetrySavesLatest` (use a real blocked/unwritable temporary store or deterministic failing store with observed real service state).
- [ ] **Step 2: Run** `dotnet test tests/AFKtor.Presentation.Tests -c Release`; verify new behavior fails first.
- [ ] **Step 3: Build the production views/view models**, not a copy of the programmatic spike. Add Fluent styles, compact “Champion plans” heading/navigation, five role rows with readable labels, ranked full-name chips and counts, and a persistent right catalog without repeated initial badges. Use “Browse by role: All” for the filter and a separate “Add to: Top · Picks” line above results. Include one contextual Earlier/Later/Remove strip, empty/no-results states, and a persistent truthful save-status area separate from transient action feedback. A duplicate message includes role/list/position. Explain empty/exhausted plans once without implying live availability. Names remain readable. Use standard focusable controls and compiled bindings. Keep IDs/revision counters in diagnostics only. Catalog filtering never restricts plan eligibility.
- [ ] **Step 4: Add preview composition.** `dotnet run --project src/AFKtor.Preview` uses the real views with an isolated temporary directory, sample plans, and no production runtime/discovery/automation. Explicit `--data-dir <directory>` permits a developer-selected local preview store; default must never use the Windows profile location. The preview disposes its one service on exit and must not delete user-specified directories.
- [ ] **Step 5: Verify** control tests and launch the actual preview on Linux. Check all five role headings, blank search, Top/Tryndamere, click target/add/duplicate, keyboard alternatives, saved/unsaved/retry and resize. The default window fits the work area; lanes remain single-line with obvious horizontal overflow/reveal and counts, and the catalog scrolls independently. Check full names and independent filter/destination changes at scaled/small work areas; do not copy the prototype’s large minimum dimensions or wrapping behavior. Update exact CLI instructions and commit `Add Avalonia champion workspace and isolated preview`.

## Task 03: Direct manipulation, scrolling and focus

**Files:** Create `src/AFKtor.Presentation/Editing/{PlanDragPayload.cs,PlanDropTarget.cs,PlanDropController.cs,PlanDragBehavior.cs}`, `tests/AFKtor.Presentation.Tests/{PlanDropControllerTests.cs,PlanDragInputTests.cs}`; modify the existing champion views/styles and view model only for this interaction layer.

**Interfaces:**
- Consume Task 02 view model and Task 01 Apply results.
- Produce `record PlanDragPayload(int ChampionId, DraftRole? SourceRole = null, PlanList? SourceList = null)` (null source=catalog) and `record PlanDropTarget(DraftRole Role, PlanList List, int? BeforeChampionId = null)`.
- Produce `PlanDropController(ChampionPlansViewModel editor)` and `Commit(PlanDragPayload payload, PlanDropTarget target): PlanEditResult`. Same source/target list selects Reorder; every other destination selects Insert. Pointer coordinates never escape the view/input layer as persistent list indices. Preserve complete source/destination identities; do not use the old snapshot/index-based Add/Remove/Move helpers as the new interaction implementation. All pointer/keyboard mutations go through Task 01 Apply and its atomic current-list gate.

- [ ] **Step 1: Add failing behavior tests.** Verify cross-list copy and no intermediate revisions, same-list reorder, destination duplicate highlighting, stale source/anchor cancellation, and cancelled/outside drops. Add headless real-input cases `SearchDeleteNeverEditsPlans`, `PlanDeleteUsesSameRemoveCommand`, `EscapeCancelsDragWithoutRevision`, `DragPastOverflowRevealsInsertionWithoutCommitting`, `PlanReorderRetainsFocusOnAffectedChampion`, `CatalogAdditionAndDuplicateDoNotStealBrowseFocus`, `DestinationSelectionAndKeyboardFocusAreDistinct`, `FocusedEntryKeyboardCommandIgnoresChangedInsertionDestination` (put champion 86 in two lanes, keep the first occurrence focused, change insertion destination, press Delete and assert only the focused occurrence is removed).

```csharp
view.FindControl<TextBox>("SearchBox")!.Focus();
window.KeyPress(Key.Delete);
Assert.That(plans.Current.Revision, Is.EqualTo(beforeRevision));
Assert.That(plans.Current.For(DraftRole.Top).Picks, Is.EqualTo(beforePicks));
```

Fixtures host the actual view in a shown headless Window; inspect actual focus and lists. Add no test-only branches to production controls.
- [ ] **Step 2: Run** `dotnet test tests/AFKtor.Presentation.Tests -c Release --filter 'FullyQualifiedName~PlanDrag|FullyQualifiedName~PlanDrop'`; observe the missing interaction behavior fail.
- [ ] **Step 3: Implement native drag/drop** using Avalonia 12 DataTransfer with an in-process payload. Use an 8 logical-pixel movement threshold, narrow insertion marker, correct copy/move effects plus visible Copy/Move feedback, cancellation cleanup and horizontal lane autoscroll. Destination lane outline, selected chip and keyboard focus must remain distinguishable from the drop marker; do not communicate by color alone. Hover is presentation-only; commit once on valid drop. Reject unsupported external payloads. Recheck stable IDs against current state at commit, and reveal/select existing entries on duplicates without stealing browsing focus.
- [ ] **Step 4: Wire keyboard/click behavior** to the same commands: toolbar Earlier/Later/Remove against SelectedEntry, and Alt+arrows/Delete against the actual focused PlanEntryKey only when a plan entry owns the action context. Changing insertion destination must not retarget either subject, even when both lanes contain the same champion. Do not intercept text-editing keys in search/settings. Keep one toolbar labeled with its full subject (for example “Top · Picks · Garen · Priority 1”). Preserve plan-entry focus after reorder, and choose the neighboring entry/lane Add control after removal; catalog-origin add/duplicate keeps browsing/search focus. Announce destination/name/position in the independent action-feedback area, without overwriting persistent saving status.
- [ ] **Step 5: Verify** focused tests, presentation suite and actual Linux pointer operations, including long-lane insertion, cancelled drag, cross-role copy, picks→bans copy, duplicate and keyboard reorder. Commit `Add accessible plan drag and copy interactions`.

## Task 04: Extract a UI-independent runtime without changing behavior

**Files:** Create `src/AFKtor.Runtime/{AFKtor.Runtime.csproj,AppRuntime.cs,AppRuntimeSnapshot.cs,ILeagueClientDiscovery.cs,WindowsLeagueClientDiscovery.cs}`, `tests/AFKtor.Tests/AppRuntimeTests.cs`; modify existing `TrayApplicationContext.cs` and `SettingsForm.cs`, test project references and `AFKtor.slnx`. Keep current WinForms windows usable during this task.

**Interfaces:**
- Runtime targets net10.0 and references Core/Diagnostics. `ILeagueClientDiscovery.DiscoverAsync(CancellationToken token): Task<LcuConnection?>` wraps the current process/lockfile logic.
- Produce `AppRuntime(string dataDirectory, ILeagueClientDiscovery discovery, TimeProvider? timeProvider = null, HttpMessageHandler? transport = null, Func<ChampionCatalog, DiagnosticCollector, RolePlanService>? createPlans = null)`; production factory `AppRuntime.CreateWindows(string dataDirectory): AppRuntime` uses the current loopback-only handler and Windows discovery. Injected transport/time and the optional plan-service factory are composition/test seams, not user options. The default factory creates the one existing file-backed autosaving RolePlanService at the legacy paths; production CreateWindows uses that default. Runtime owns the single service returned by the factory, including its disposal.
- Expose `Plans: RolePlanService`, `Catalog: ChampionCatalog`, `Snapshot: AppRuntimeSnapshot`, `StartAsync(): Task`, `SetArmed(bool armed): void`, `StopAsync(): Task`, and idempotent `DisposeAsync(): ValueTask`.
- `record AppRuntimeSnapshot(bool Connected, bool Armed, string ReadyStatus, TimeSpan? Remaining, DraftState Draft, BanState Ban, PickState Pick)` uses the current Core states. Views sample this; they do not own network timers or controllers. StartAsync starts the background owner and returns without waiting for its lifetime; StopAsync is terminal/idempotent and drains it. The first Stop/Dispose request atomically installs one cached shutdown task under the lifecycle gate; all concurrent/subsequent StopAsync callers and DisposeAsync wrappers await that same completion, including its outcome. An already-stopping flag alone is not completion. Start/Stop state transitions use the same lifecycle gate: repeated Start while Running is a no-op, but Start after stopping begins throws ObjectDisposedException and never starts discovery. A stopped/disposed runtime is not restarted by reopening a window. Stop before Start still releases constructed services.

- [ ] **Step 1: Write failing lifetime tests** using temporary data directories, controlled discovery/transport and TimeProvider. The test factory constructs a real RolePlanService with `startWorker:false` and a barrier wrapper around a real RolePlanFileStore, so the dirty final save cannot be consumed by an earlier autosave tick. Use an asynchronously observed read-cancellation barrier plus explicit release gates; no timed sleeps. `StartsDisarmedAndReadsExistingSharedSettings`, `RefreshWaitsTwoSecondsAfterPriorCycleWithoutOverlap`, `StopCancelsHeldReadAndDrainsOnce`, `TimingPublicationUpdatesLiveReadyController`, `RepeatedStartWhileRunningIsIdempotent`, `ConcurrentStopAndDisposeAwaitReadAndFinalSave`, `StopRacingStartNeverLeavesBackgroundOwner`, `StopBeforeStartDisposesConstructedServices`. Test real monitor/controller paths; do not merely assert a fake callback count.

```csharp
await runtime.StartAsync();
Assert.That(runtime.Snapshot.Armed, Is.False);
runtime.SetArmed(true);
Assert.That(runtime.Snapshot.Armed, Is.True);
var stop = runtime.StopAsync();
var dispose = runtime.DisposeAsync().AsTask();
var repeatedStop = runtime.StopAsync();
// Fixture holds completion of a real monitor read and then the final store write.
await readCancellationObserved; // wait for cancellation delivery before asserting pending callers
Assert.That(stop.IsCompleted || dispose.IsCompleted || repeatedStop.IsCompleted, Is.False);
releaseReadCompletion();
await finalSaveStarted; // deterministic store barrier, not a timed sleep
Assert.That(stop.IsCompleted || dispose.IsCompleted || repeatedStop.IsCompleted, Is.False);
releaseFinalSave();
await Task.WhenAll(stop, dispose, repeatedStop);
var (reloadedPlans, _) = fileStore.Load(catalog);
Assert.That(reloadedPlans.For(DraftRole.Top).Picks, Is.EqualTo(latestUnsavedPicks));
```

- [ ] **Step 2: Run** `dotnet test tests/AFKtor.Tests -c Release --filter FullyQualifiedName~AppRuntimeTests`; observe missing behavior fail.
- [ ] **Step 3: Move composition/lifetime** from the WinForms context into Runtime. Preserve shared catalog/plan service, one transport, diagnostic directories, timing subscription, one controller and both monitors. Retain the current discovery logic exactly except interface adaptation. Use a background asynchronous owner: wait 2 seconds, await both refreshes, then wait 2 seconds again; never overlap cycles. UI status reads do not block on network/disk. Both native draft gates remain explicitly false. The cached shutdown owner invokes child teardown once in order; no caller reports completion before the final save attempt and resource release finish. A failed save preserves the existing failure/reporting semantics. Do not redesign every child service: prevent concurrent child disposal through this single runtime owner.
- [ ] **Step 4: Adapt the existing WinForms host** to use this runtime and snapshot while keeping startup/tray/editor/settings behavior intact. Adapt SettingsForm to `SettingsForm(RolePlanService plans, Func<AppRuntimeSnapshot> snapshot)` for countdown/status instead of its concrete controller parameter. Window lifetime must not start/stop runtime. Explicit Stop cancels immediately, drains refresh/monitors, performs the final plan save, and releases diagnostics/transport once, even under concurrent/repeated exit requests. Both WinForms Exit and Dispose await the shared runtime shutdown completion; neither separately disposes its children. Avoid synchronously blocking a UI continuation on work that requires that UI context.
- [ ] **Step 5: Verify** runtime tests, full existing suite and Release Windows-targeting build. Confirm the old UI still cross-compiles and current persistence/diagnostic/ready-check tests stay green. Commit `Separate application runtime from window lifetime`.

## Task 05: Switch production shell, preserve Settings, and integrate

**Files:** Create `src/AFKtor.Presentation/{Views/MainWindow.axaml,Views/MainWindow.axaml.cs,Views/SettingsView.axaml,Views/SettingsView.axaml.cs,ViewModels/SettingsViewModel.cs,ViewModels/MainWindowViewModel.cs}`, `src/AFKtor.Tray/{App.axaml,App.axaml.cs,TrayShell.cs,Assets/afkter.ico}`, `tests/AFKtor.Presentation.Tests/{SettingsViewTests.cs,ShellLifetimeTests.cs}`; modify `Program.cs`, `AFKtor.Tray.csproj`, solution/docs; remove obsolete `RolePlansForm.cs`, `SettingsForm.cs`, `TrayApplicationContext.cs`, and the original discovery file only after their replacements are connected.

**Interfaces:**
- Consume Runtime from Task 04 and production editor from Tasks 02/03.
- Produce `enum AppPage { Champions, Settings }`; `MainWindowViewModel(ChampionPlansViewModel champions, SettingsViewModel settings)` and `Navigate(AppPage page): void` without rebuilding either service/view model.
- Produce `SettingsViewModel(RolePlanService plans, Func<AppRuntimeSnapshot> snapshot)` preserving all current controls and invalid-input behavior.
- `TrayShell(AppRuntime runtime, Action<AppPage> showPage, Func<Task> exit)` is a UI adapter, not a runtime owner. Main App owns one window/runtime and explicit shutdown.

- [ ] **Step 1: Add failing shell/settings tests.** Pin hiding versus quitting and state retention using the actual window/view models and a controlled real runtime:

```csharp
var service = runtime.Plans;
main.Navigate(AppPage.Settings);
window.Close(); // production close handler cancels Close and hides
Assert.That(window.IsVisible, Is.False);
Assert.That(runtime.Snapshot.Armed, Is.True);
main.Navigate(AppPage.Champions);
window.Show();
Assert.That(runtime.Plans, Is.SameAs(service));
Assert.That(champions.SearchText, Is.EqualTo("trynd"));
```

Add `RepeatedPageShortcutReusesWindow`, `RepeatedShowHideAndNavigationDoNotAccumulateRefreshTimers`, `SettingsEditsSharePlanRevisionAndErrorState`, `IncompleteTimingTextIsNotOverwrittenByRefresh`, `ResetPublishesOneRevision`, `ExitDuringHeldReadCancelsAndSavesLatest`, `OpenHideReopenRetainsUnsavedPlanAndRetryRecovers`, `LegacyStoredPreferencesLoadBeforeFirstRender`. Mock native tray plumbing only where headless OS APIs are unavailable; test its routing against real app state.
- [ ] **Step 2: Run** presentation tests; observe missing shell/settings behavior fail before implementing.
- [ ] **Step 3: Replace startup with Avalonia.** Retarget the existing Tray project to net10.0 with Avalonia.Desktop; retain its technical assembly identity and Afkter product metadata. Production startup is Windows-only and tray-resident/disarmed. Linux developers run the separate Preview executable, not real discovery. Main window contains only Champions and Settings navigation; matching tray shortcuts activate/reuse it on the requested page. Close hides and stops presentation refresh without stopping the runtime; reopening resumes the existing refresh owner. Exit disables UI commands and awaits the shared runtime shutdown completion before invoking explicit Avalonia lifetime shutdown. Concurrent exit requests await that same completion. Add an original simple bundled icon without external artwork dependencies.
- [ ] **Step 4: Migrate Settings to real shared Avalonia controls.** Preserve ready delay 0–30 seconds; pacing/hover 0–5000 ms; urgent 1000–10000 ms; safety 250–1000 ms strictly below urgent; all three policy descriptions; initially collapsed advanced section; immediate valid publication, coherent timing reset, error/recovery/retry and countdown. No save/cancel buttons, arming persistence or eligibility changes. Avoid overwriting a focused invalid/incomplete field during refresh.
- [ ] **Step 5: Integrate and remove obsolete UI only now.** Update preview to open the shared MainWindow with sample runtime snapshot and no automation if Settings is included; no production runtime is constructed there. Preserve Windows `%LOCALAPPDATA%/AFKtor` files and diagnostics contracts. Update ADR 0002 to reflect approved unified host. Update README with Linux preview, Windows app, build and known native checks; update ticket resolutions and retain prior comments.
- [ ] **Step 6: Verify one integrated release.** Run `dotnet test AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`, `dotnet build AFKtor.slnx -c Release -p:EnableWindowsTargeting=true`, and `git diff --check`; require zero test failures/build warnings/errors. Run the actual Linux shared editor and exercise the approved flow, overflow, search-key isolation and save recovery. Record any unperformed Windows tray/keyboard/DPI/Narrator checks as pending. Original draft ticket 07 and native write gates are unchanged. Commit `Switch Afkter to the Avalonia desktop shell`, then perform a fresh whole-branch review and resolve material findings before the user's authorized push/checkpoint.

## Self-Review and Handoff

Spec coverage: Tasks 01–03 cover catalog, role data, ordered interactions, stable IDs, accessibility and preview. Task 04 preserves automation/lifetime contracts. Task 05 preserves navigation, Settings, shutdown, storage/diagnostics and packaging commands. Review Focus items each have named owning tests above. Selection uses the complete role/list/champion key, separate from insertion destination and catalog selection. Concurrent Stop/Dispose callers share a completion task, not just a repeated-execution guard. Interface names/types are shared literally across tasks; runtime remains independent of Avalonia and preview remains independent of production discovery.

This is an implementation plan, not completed code. No checkboxes are checked. The player approved the design; review this task decomposition before execution. Continue the established fresh-implementer/worktree and independent-review method unless the player requests a different method, and preserve their limit on how many tasks may start per batch. No production migration or native-readiness claim is authorized by drafting this plan alone.
