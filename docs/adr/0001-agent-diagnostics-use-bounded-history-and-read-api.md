# Agent diagnostics use bounded history and a read-only local API

AFKtor diagnostics primarily serve AI development agents. We chose structured,
persistent event history plus a read-only local HTTP API over UI/console-only
logging, files alone, or an API without durable history: agents need both current
state and evidence that survives process exit. The same cross-platform .NET
service supports Linux unit/integration tests and the Windows app; a visual debug
viewer is deferred and will consume the same model.

History is always collected, with both seven-day retention and a 20 MB total
history limit. The API starts with the app, binds to localhost, and uses a
generated token plus local discovery. Normal state transitions and decision
inputs are evidence as well as errors, but credentials and raw authenticated
responses are excluded. Diagnostics are observers: backpressure, storage or API
failure must degrade evidence rather than block or change application behavior.
These choices were agreed in the design interview and accepted for the approved
four-ticket breakdown. The [diagnostics spec](../../.scratch/agent-diagnostics/spec.html)
and its linked ticket index record the scope, defaults, and delivery gates.
