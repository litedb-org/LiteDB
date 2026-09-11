# Memory-management implementation TODO

This checklist tracks the implementation and validation of
`docs/memory-management-proposal.md` revision 5. The separate opt-in
`Query.Parameterized` API discussed in section 5.8 is intentionally outside
this memory-fix PR; revision 5 says the bounded cache ships alone and treats
parameterized helpers as a later compatibility decision.

## Baseline and CI coverage

- [x] Record a clean baseline build and test result (Release build succeeds;
  net8.0: 322 passed, 6 skipped; net461/net481 require Mono on this host).
- [x] Ensure internal cache tests compile and run in Release CI.
- [x] Establish compilation of all supported target frameworks as the baseline.

## Phase 1: frame correctness and ownership

- [x] Add explicit `Free`, `Loading`, `Readable`, and `Writable` frame states.
- [x] Serialize frame/index/pin transitions under one cache lock.
- [x] Publish loads before I/O, wait by key, and clean up failed/losing loads.
- [x] Copy readable pages to writable pages while eviction is excluded.
- [x] Track the last pin release and concurrent re-pin correctly.
- [x] Make `MoveToReadable` key collisions invariant failures without overwrites.
- [x] Record WAL/new-page positions before release and release on callback/write failure.
- [x] Preserve checkpoint invalidation as a distinct operation.
- [x] Add generation, snapshot-epoch, writable-ownership, and poison checks.
- [x] Replace inferred/LINQ cache counts with maintained counters.

## Phase 2: expression cache

- [x] Bound compiled scalar and enumerable delegates to 1,000 total entries.
- [x] Make cache admission and its maintained count concurrency-safe.
- [x] Report compiled-expression count through `$database`.

## Phase 2b: stream ownership and capacity

- [x] Dispose engine-owned data, log, and sort streams without disposing caller streams.
- [x] Trim owned in-memory log capacity after checkpoint through the stream abstraction.
- [x] Dispose partially constructed disk services/pools on constructor failure.
- [x] Dispose file streams when hidden-file attribute setup fails.
- [x] Dispose the transaction monitor's thread-local slot.
- [x] Drop cache segments immediately on cache disposal.

## Phase 3: transaction retention and safepoints

- [x] Add `CacheSize` and `TransactionPageLimit` engine settings and connection-string keys.
- [x] Apply 64 MiB file and 8 MiB memory-backed defaults plus minimum/rounding rules.
- [x] Replace the shared/extensible transaction budget with a fixed per-transaction limit.
- [x] Wire a safepoint delegate through snapshots and page-backed services.
- [x] Bound sorted output, grouped replay, includes, index filters/misses, and absent deletes.
- [x] Bound vector search, materialization, writes, build, and drop with safe reloads.
- [x] Bound `DropIndex` traversal and preserve persisted index correctness.
- [x] Update transaction/cache `$database` fields and existing reflection-based tests.

## Phase 4: bounded elastic page cache

- [x] Use an 8-page first segment and 128-page subsequent segments.
- [x] Track segments, intrusive free lists, and occupancy/releasability metadata.
- [x] Implement CLOCK eviction, persistent hand, reference bits, and scan metrics.
- [x] Never allocate beyond the rounded target while an idle victim exists.
- [x] Permit/report overflow only for pinned, writable, or loading pressure.
- [x] Prefer reclaimable segments during trim and terminate when no progress is possible.
- [x] Release only fully free excess segments while keeping one spare.
- [x] Report allocation, retention, state, eviction, release, overflow, and hit/miss metrics.
- [x] Rewrite cache tests that encode the old growth ramp/hysteresis.

## Deterministic cache and failure tests

- [x] Concurrent same-key loading loses no frame.
- [x] Failed loading returns its frame and unblocks/retries waiters.
- [x] Eviction/reuse cannot pin a stale frame or fool a woken waiter.
- [x] Writable copying excludes concurrent frame reuse.
- [x] Checkpoint invalidation reads new content and preserves within-limit segments.
- [x] All-pinned trim terminates; release and re-pin update segment liveness.
- [x] Idle referenced frames evict on a second chance without target overshoot.
- [x] Scan-budget exhaustion continues to a victim without allocation.
- [x] Peak segments become collectable after release.
- [x] Scattered pins report retained segment bytes without unsafe release.
- [x] CLOCK prefers releasable segments, packs allocations, and protects hot pages.
- [x] WAL callbacks run while frames remain pinned; failure paths release pins.
- [x] Retained node access after safepoint fails ownership checks in test/debug builds.
- [x] Released frames are poisoned and generation-invalidated in test/debug builds.

## Engine and memory regression tests

- [x] Streamed scans remain at/below the rounded cache target with zero final pins.
- [x] Half-consumed cursors remain bounded by the transaction threshold plus one document.
- [x] Add one pin-bound regression test for every safepoint-audit row in section 5.3.
- [x] Vector and index safepoint tests checkpoint/reopen and verify actual contents.
- [x] Bulk writes reuse frames instead of ratcheting allocation.
- [x] Memory-database checkpoints release owned WAL capacity (plain and encrypted).
- [x] Owned temp streams delete spill files; caller-owned streams stay open.
- [x] Expression-cache cap holds under concurrent unique expressions.
- [x] Repeated literal `Query.EQ` values cannot cause unbounded retained delegates.
- [x] Disposed databases/engines/caches/segments are collectable after forced GC.
- [x] Suspended cursors retain only their referenced released segment until disposed.

## Performance and final verification

- [x] Add or update scan, point lookup, bulk write, vector, concurrent-reader, trim, and index-build benchmarks.
- [x] Run focused concurrency/cache/safepoint/expression/stream tests repeatedly.
- [x] Run memory/stress scenarios and inspect `$database` accounting invariants.
- [x] Run the full Release solution build and test suite.
- [x] Re-run leak/collectability checks after all fixes.

## Delivery

- [x] Split changes into reviewable commits where practical.
- [x] Push the implementation branch and open a PR against `litedb-org/LiteDB`.
- [ ] Monitor every CI job, diagnose failures, push fixes, and obtain green CI.
- [ ] Run a final post-CI regression and memory-leak verification.
