# Champion-plan UI: two independent architecture reviews

November 8, 2026. Independent research record. The player subsequently selected Avalonia; see [ADR 0002](../adr/0002-avalonia-for-plan-editor.md). A throwaway interaction prototype was authorized first. The player subsequently approved the unified-shell production design; its implementation plan and task decomposition are awaiting review before execution.
The player requested two fresh-context researchers who were not told any prior
candidate stack or recommendation. Both were supplied the agreed product goals,
existing .NET core, Windows target, Linux development workflow, offline editing,
AI-authored maintenance, and preference for a simple polished interface. They
researched primary documentation independently and did not consult each other or
the redesign discussion documents.

## Independent conclusions

Both recommended Avalonia with C#/XAML, a small MVVM presentation layer, and the
existing Core remaining authoritative in the same process. Their judgment gives
particular weight to running the actual production editor view on Linux, avoiding
a cross-language state/command bridge, and retaining existing immutable revisions,
validation and autosave. This agreement supports further evaluation; it does not
prove final usability or guarantee framework-specific implementation quality.

Researcher A compared Avalonia, Blazor Hybrid, and WPF. Researcher B also examined
web UI in the existing Windows host, Electron, and Tauri. Both identified web UI
hosting as a credible alternative with strong styling and browser tooling, but
with a host/renderer boundary and Windows-specific embedding outside everyday
Linux validation. WPF retains direct C# integration but cannot run the view on Linux.
Electron/Tauri introduce additional process/runtime ownership around a working
.NET core, without a demonstrated product requirement for it.

## Host integration: a real tradeoff remains

Researcher A initially proposed preserving the WinForms tray and embedding a shared
Avalonia view; researcher B preferred migrating the composition/tray lifecycle to
Avalonia. A focused factual followup verified a free supported embedding route:

- [Official Windows/WinForms hosting documentation](https://docs.avaloniaui.net/docs/platform-specific-guides/windows/#using-avalonia-in-windows-forms)
- [WinFormsAvaloniaControlHost API](https://docs.avaloniaui.net/api/avalonia/win32/interoperability/winformsavaloniacontrolhost)
- [Avalonia.Win32.Interoperability package](https://www.nuget.org/packages/Avalonia.Win32.Interoperability), MIT-licensed; versions must match the framework.

Embedding therefore does not require a paid Avalonia product, but retains two UI
lifecycles and requires correct initialization/input routing. A Linux development
host would run the same shared view separately. Migrating the small host shell
avoids that long-term mixed-framework boundary, but broadens the initial scope to
tray activation, window closing/reopening, timers, discovery and shutdown ownership.
Neither route requires rewriting automation or creating two independent writers
for the same plan file. The final host choice is not approved yet.

## Verified capabilities and practical limits

- [Supported platforms](https://docs.avaloniaui.net/docs/supported-platforms): Windows and Linux.
- [Drag/drop primitives](https://docs.avaloniaui.net/docs/input-interaction/drag-and-drop): building blocks, not a finished accessible reorder interaction.
- [Testing](https://docs.avaloniaui.net/docs/testing/): headless input/control and visual testing support.
- [Compiled bindings](https://docs.avaloniaui.net/docs/data-binding/compiled-bindings): useful compile-time checking for AI-authored bindings.
- [Accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility): standard controls and automation peers still require deliberate labels, focus and keyboard alternatives.
- [Tray icons](https://docs.avaloniaui.net/controls/navigation/trayicon) and [application lifetimes](https://docs.avaloniaui.net/docs/fundamentals/application-lifetimes): support residence independent of editor windows.

Avalonia does not automatically create good UX. Avoid bespoke canvas controls,
large navigation frameworks or complicated reactive state. Windows-specific
keyboard, drag/drop, DPI, Narrator, tray and packaging behavior still needs occasional
Windows checks. Free framework licensing must not be confused with optional paid
professional tools/components.

All UI candidates separately need role metadata: the current champion catalog
contains IDs/names, and champion class tags are not lane assignments. Role metadata
and optional portrait sourcing remain open decisions independent of the framework.

## Coordinator's revised proposal

Favor Avalonia for the next representative UI prototype, because the player values
long-term architecture and accepts additional initial work. Compare actual catalog
browsing, drag/drop feedback, keyboard editing and five-role layout before approving
implementation. Keep the existing C# core and save format. A unified Avalonia host
is the leading long-term proposal; preserve current behavior during any host change.
The player subsequently selected Avalonia, recorded in ADR 0002. The player subsequently approved the unified-host production design; implementation-plan review is pending. The throwaway preview does not replace the existing app.
