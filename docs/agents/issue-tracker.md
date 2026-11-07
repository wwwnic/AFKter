# Issue Tracker

AFKtor uses local HTML for specs, tickets, and Wayfinder maps. Follow these paths when a skill gives Markdown defaults for tracker artifacts.

## Publish and fetch

- Publish each spec at `.scratch/<feature-slug>/spec.html`.
- Publish one ticket per file at `.scratch/<feature-slug>/issues/<NN>-<slug>.html`. Copy the [ticket template](templates/ticket.html), replace its placeholders, and number tickets from `01` in dependency order.
- Link each blocker to its ticket using a relative URL. Enter **None** when there are no blockers.
- Resolve a ticket number within its feature directory. Read the full file, including its comments, before updating it.
- Record the current triage label in **Status**. Append dated entries under **Comments**, keeping earlier entries in order.

## Wayfinder

- Write the map to `.scratch/<effort-slug>/map.html` with **Notes**, **Decisions so far**, and **Fog** sections. Use the same numbered ticket paths for its children.
- Record each child’s type (`research`, `prototype`, `grilling`, or `task`) and Wayfinder state in separate **Type** and **Wayfinder state** fields.
- Claim the lowest-numbered open, unclaimed ticket whose blockers are resolved. Save `claimed` before starting work.
- To resolve a child, add an **Answer** section, save `resolved`, and add a brief summary and link to the map’s **Decisions so far**.
