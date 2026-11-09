# Use Avalonia for the champion-plan UI

We selected Avalonia for Afkter’s redesigned champion-plan interface after two independent architecture investigations. It keeps the UI close to the existing C# core and lets the actual editor run during Linux-based development; the player also wants to explore this framework. React with modern WebView2 remained a credible alternative, but its additional UI bridge and Windows hosting boundary were less attractive for this workflow.

Start with a throwaway interaction prototype using local sample data. The selected framework does not approve the final layout, a production UI replacement, or a particular tray-host migration. Preserve existing automation, saved plans, live revision semantics and autosave when the production design is implemented.
