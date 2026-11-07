# Ticket 01 fix round 1 — scoped re-review

Verdict: **ADDRESSED**.

Reviewed immutable fix range `422e252..d0e448a` against the supplied finding and review package. The range changes only `tests/AFKtor.Tests/DiagnosticIsolationTests.cs` (42 insertions, 4 deletions). No production files changed.

## Finding disposition

The previous test required `Parallel.For` to create contention, although a single-processor runtime can correctly serialize all producer calls. The fix removes that scheduling assumption.

- Lines 34–44 isolate invalid-observation accounting: one producer loss, unchanged startup watermark and connection state, incomplete history, and no sink queue entries.
- Lines 47–80 hold the collector's actual private `gate` on the test thread and invoke the producer on a dedicated thread. The signal must arrive before the owning thread releases the gate. This establishes contention independently of processor count and thread-pool scheduling; it would fail if the producer waited for the gate.
- The gate is released before assertions and the producer is joined with a bounded timeout. The test verifies exactly one dropped observation, no sequence/projection advancement for the dropped observation, incomplete history, and successful publication after release.

The reflection lookup matches the collector gate at the reviewed head. Its deliberate coupling is confined to the test and avoids adding a production test hook.

## New breakage assessment

No new blocking findings in the fix diff. The test no longer relies on probabilistic parallel contention. Its two-second waits bound scheduling and join failures without requiring multiple CPUs. Production behavior and the existing throwing/hung-sink tests are unchanged.

## Evidence

Inspected the task brief, the final “Review fix round 1” report section, the review package, the exact committed diff, the collector implementation at `d0e448a`, and the supplied logs:

- `task-1-fix1-red.log`: original single-CPU test failed as reported (expected producer loss greater than 1, actual 1).
- `task-1-fix1-default.log`: isolation tests **4 passed, 0 failed, 0 skipped**.
- `task-1-fix1-single-cpu.log`: isolation tests **4 passed, 0 failed, 0 skipped** with the reported `DOTNET_PROCESSOR_COUNT=1` invocation.
- Reviewer ran `git diff --check 422e252 d0e448a`: clean.

The test results above are inspected implementation evidence, not a new reviewer execution. No full suite or build was rerun, consistent with this scoped test-only review. No source, index, or HEAD edits were made.

## Outside-diff observations

None raised. Broader ticket and combined-review conclusions remain outside this re-review.
