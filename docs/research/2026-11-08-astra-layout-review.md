# GPT-6-ASTRA champion-editor layout review

The player requested a fresh GPT-6-ASTRA layout recommendation, then authorized
incorporating useful recommendations. The model reviewed the approved design and
prototype source; it did not inspect a rendered UI or implement changes.

It recommends retaining the five-role board beside a persistent catalog. A
selected-role-only editor would hide other plans and weaken the agreed overview
and copying workflow. The following refinements were incorporated into the design,
implementation plan, and draft tickets 02/03; dependencies/statuses are unchanged.

1. **Separate browsing from destination.** Label the filter “Browse by role: All”
   and put “Add to: Top · Picks” directly above catalog results. Pair that line with
   the destination lane outline and distinct accessible names. Neither control
   changes the other implicitly; color alone does not convey the difference.
2. **Prioritize readable champion names.** Use a compact “Champion plans” heading,
   Champions/Settings navigation and a short priority explanation. Omit repeated
   initial badges from catalog rows; retain names and a restrained add affordance.
3. **Make overflow deliberate.** Single-line ranked lanes show counts and obvious
   horizontal overflow, revealing the affected entry/insertion position. Do not
   wrap or shrink names to squeeze everything in. Keep the catalog accessible
   when a small work area requires board scrolling; test scaled window sizes.
4. **Separate interaction states.** Outline destination lanes, highlight selected
   entries, retain visible keyboard focus and show a narrow insertion bar. Drag
   feedback names Copy versus Move. One stable toolbar identifies its full subject,
   for example “Top · Picks · Garen · Priority 1”. Catalog-origin additions retain
   browsing focus; plan reorder retains entry focus; removal selects a neighbor
   or the empty lane's Add control.
5. **Separate outcomes from persistence.** Duplicate feedback names role, list and
   priority, such as “Already in Top picks at #2”. Persistent Saved/Saving/error
   status remains visible and is never overwritten by transient action messages.
   Save failure explicitly means changes are active but not saved. Empty lists
   offer Add champions; explain once that empty/unavailable plans supply no extra
   fallback, without pretending the offline editor knows live draft availability.

These refinements retain existing agreed semantics: offline named browsing,
independent role filter, copies between lists, no duplicate destination entries,
explicit removal and live autosave. No role tabs, automatic filter coupling,
mandatory portraits, expansion modes or new configuration controls were added.
This report is design guidance, not implementation or native acceptance evidence.
