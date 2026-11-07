# Ticket 02 independent review

Reviewed base `d0e448a` through head `b090dec` using the review contract, brief, implementation report, and supplied review package. This is a scoped, read-only review of implementation; the separately requested review file is the only write. I did not rerun the reported 126-test suite, 19 focused tests, or Windows cross-build.

**Spec-compliance verdict: Fail — one Important finding.** The retained-event API can report available storage and a complete current-run page after its sink has been disabled and known history is missing.

**Task-quality verdict: Changes required.** The implementation has useful bounded recovery, retention, ownership, API integration and explicit degradation machinery, with focused automated coverage and documentation. The merger does not carry collector sink failure/loss into its completeness calculation, and calculates internal sequence gaps only on the requested page. This leaves an important failure path untested.

## Findings

### Important — Current history can conceal a disabled sink and a known hole

`src/AFKtor.Diagnostics/RetainedDiagnosticHistory.cs:25` and `:29`–`:41`. Trigger: a store write exceeds the collector's 500 ms deadline or throws, so `DiagnosticCollector.DrainAsync` disables the sink and counts lost queued events. The store can subsequently report `available` after a successful maintenance/read, while the collector cannot resume writes. After enough live events evict memory, assume disk retains sequences 1–100 and memory retains 1000–2023. Querying `afterSequence=2023` produces an empty page: disk's own prefix is contiguous, `firstAvailable` is 1, and the requested page has no records to check. The page therefore reports `storage: available`, no gaps and `currentRunMemoryAndDisk`, despite the established hole 101–999. The same misleading availability applies before memory eviction. `/state` separately protects its storage field against `sinkUnavailable`, but the events and runs endpoints do not use that protection. This violates the requirement to distinguish an empty tail from established loss or unavailable persistence. It also makes the API appear to have resumed persistence when the sink stays disabled for the run.

Suggested fix: propagate collector sink-disabled/queue-loss health into the merger's storage and completeness metadata, and detect known discontinuities across the retained disk/memory union independently of the requested page. Preserve bounded work and do not fabricate precise missing positions for unsequenced drops. Add a focused regression that disables the sink, lets the store return to healthy, rolls the memory ring, and checks an empty tail and an early page as well as the state/runs fields.

## Scope and evidence

No Critical or Minor findings. The review inspected the patch's record framing and recovery, byte inventory and deletion accounting, time filtering and maintenance, single-owner handling, merge/pagination, background isolation, runtime wiring, and test evidence. Complete records are recovered without synthesizing shutdown; partial/corrupt framing produces safe corruption metadata. The byte budget includes managed leftovers and reserves metadata replacement space, and failure stops writes. Current/prior run state is separated and the bounded API retains explicit limits. The reported tests and cross-build are evidence, not independently rerun here.

**Cannot verify:** Native Windows NTFS sharing and crash-release behavior, open-file deletion/replacement, inherited ACLs, full or denied volume behavior, slow-filesystem shutdown, packaging, and live League transitions remain pending as stated in the report. Filesystem calls that ignore cancellation can outlive the runtime's two-second wait; native behavior remains unverified. Exact unrecorded crash-tail losses and identities after bounded tombstone eviction cannot always be determined, so `persistedPrefixOnly` is appropriately qualified. Later tickets 03/04 and the final integrated review are outside this review's scope.
