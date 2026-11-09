# Use Avalonia for the champion-plan UI

We selected Avalonia for Afkter’s redesigned champion-plan interface after two independent architecture investigations. It keeps the UI close to the existing C# core and lets the actual editor run during Linux-based development; the player also wants to explore this framework. React with modern WebView2 remained a credible alternative, but its additional UI bridge and Windows hosting boundary were less attractive for this workflow.

A throwaway interaction prototype using local sample data was explored first. The player subsequently approved the production design: a unified Avalonia shell for Champions, Settings and tray behavior, with a name-first editor and no portrait downloader in the initial scope. Preserve existing automation, saved plans, live revision semantics and autosave. The implementation plan/task decomposition remains a separate handoff; prototype styling is not frozen.
