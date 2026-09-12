# Bounded, elastic page cache for LiteDB v5 — proposal

Status: implemented, with revision 6 corrections (2026-09-12). Scope: `LiteDB/Engine/Disk/MemoryCache.cs`,
`DiskService`, `TransactionMonitor`, `TransactionService`, `EngineSettings`,
`ConnectionString`, `$database` system collection.

The implementation and measured tradeoffs are recorded in
[`memory-management-validation.md`](memory-management-validation.md).
Revision 6 retains the frame-lifetime monitor but removes expression-cache and
transaction-registry locks. `PageFramePool` owns segment metadata and
`CacheReclaimer` owns eviction policy under the frame monitor. Within-target
transaction completion skips the trim monitor using an atomic size read.

## 0. Revision history

### Revision 5 (fourth review)

Kept: the soft target, explicit frame states, segment reclamation, separate
checkpoint invalidation, the independently bounded expression cache, and the
rollout order. Changed, each point verified in the code before adoption:

- **Safepoint safety is an ownership rule, not only a generation check**
  (5.2, 5.3). A safepoint moves writable frames to readable through the log
  write and clears every snapshot; no frame becomes Free, so a generation
  check sees nothing. Reproduced by the reviewer: after a safepoint a
  retained `IndexNode` pointed at a readable frame with zero pins and a
  valid position, the snapshot held zero pages, and `SetNextNode` still
  accepted the write. `VectorIndexService.Insert` does exactly this: it
  keeps the new node across `SearchLayer` and then calls `SetNeighbors`.
  Rule: every page-backed object is dead after a safepoint; code carries
  `PageAddress`es across one and reloads. Debug checks assert writable
  ownership (frame `Writable`, page still tracked by the snapshot) as well
  as generation, and the vector tests verify content after checkpoint and
  reopen, because page counts cannot see a lost write.
- **The scan budget no longer overrides the idle guarantee** (5.2).
  Revision 4 let an acquisition allocate after examining 256 frames without
  an eviction; a 64 MB pool of 8,200 idle, referenced frames would then grow
  past the target with no pin anywhere, contradicting section 4 and the
  all-idle tests. Now a maintained idle-readable count decides whether a
  victim exists; the budget bounds the common case and is measured, and
  running out of it continues the sweep instead of allocating. "No victim
  within the budget" and "no victim exists" are distinct outcomes.
- **The safepoint audit covers unsuccessful lookups** (5.3). `Delete` runs
  `continue` for an absent id before its safepoint (`Delete.cs:38`);
  `IndexIn` seeks that miss yield nothing, so no downstream check runs
  (`IndexIn.cs:30`); and vector `Search` loads documents in a loop after the
  traversal. Measured (Release, threshold 16, extension disabled): 6,000
  absent ids retained 111 transaction pages through `Delete` and 111 through
  an indexed `IN`. Checks go per attempted deletion, per `IN` value and per
  materialized vector result. `IndexService` holds a `Snapshot`, not a
  transaction, so the safepoint is wired through the snapshot.
- **The last-release protocol is specified** (5.2). `PageBuffer.Release`
  only decrements today; the segment busy counts and occupancy buckets must
  learn when the last pin disappears. Phase 1 does release bookkeeping
  under `_sync`; the lock-free `1 → 0` fast path is an optimization the
  concurrent-reader benchmark has to justify.
- **Parameterizing the `Query.*` helpers is a separate compatibility
  decision** (5.8). `BsonExpression.Source` and the implicit string
  conversion are public (`BsonExpression.cs:147`); `string p =
  Query.EQ("value", 7)` is self-contained today and would carry an unbound
  `@0` afterwards (reproduced: the reparsed parameterized text evaluated to
  false). The cache cap is the memory fix and ships alone; parameterized
  helpers become an opt-in API with migration guidance.
- **Checkpoint invalidation releases only what exceeds the limit** (5.2).
  Auto-checkpoint runs every `CHECKPOINT` pragma pages (default 1,000, 8 MB
  of log); releasing every fully free segment there would drop and
  re-allocate up to 64 one-megabyte arrays per 8 MB written, gen2 churn
  today's code never has. Memory within the limit is the contract.
- **A key collision in `MoveToReadable` is an invariant failure** (5.2).
  Its only caller, `WriteLogDisk`, assigns a fresh log position, and the
  only log truncation clears the cache first under the exclusive lock, so
  the overwrite branch is unreachable; revision 4 specified it anyway with a
  condition ("the existing frame must be idle") nobody can guarantee. It is
  removed, with `ENSURE`.
- **`Evicting` is dropped**: under one lock the transition `Readable →
  Free` is atomic and the intermediate state is unobservable. The waiter
  loop is written out; re-resolving the key each iteration makes a
  generation in the wait condition unnecessary.
- **Benchmarks gain `EnsureIndex` on a large collection with log size
  recorded**: the fixed threshold rewrites every dirty hot page once per
  safepoint and an index build dirties nearly every page it touches. The
  cost is bytes and log growth, not syncs: `WriteLogDisk` ends with
  `stream.Flush()`, a buffer flush, not an fsync.
- Smaller: a current-segment pointer instead of a bucket lookup per
  acquisition; the expression cache keeps its own count instead of reading
  `ConcurrentDictionary.Count` on admission (that takes every internal
  lock); phases 1 and 4 develop on one branch with phase 1 as its own
  reviewable commit; citations fixed (`BasePipe.cs:117` is the `Include`
  load, the sorted reload is at `:177` and `:210`; the `LIKE` filter is at
  `IndexLike.cs:117`; the second index-side filter is `IndexScan`, used for
  not-equal, not an `IndexIn` with a predicate); section 8 now names phases
  3 and 4; phase 3 removes `availableSize` and `initialTransactionSize` from
  `$database` and must update the reflection helper in
  `Transactions_Tests.cs:437` that sets `_freePages`; phase 4 rewrites
  `Cache_Read_Write` and `Cache_Extends`, which encode today's ramp and
  hysteresis; "asserted on every buffer access" is now stated for
  `BasePage` only, with slices and the `0xFF` poison covering node types;
  RC3 records that `MoveToReadable` never refreshes the timestamp.

### Revision 4 (third review)

Kept: the soft target, explicit frame states, segment reclamation, separate
checkpoint invalidation, the independently bounded expression cache.
Changed:

- **Vector writes join the safepoint audit** (5.3). `VectorIndexService.Upsert`
  calls `Delete`, whose `TryFindNode` breadth-first walks the whole graph even
  for a brand-new document, and dropping the index walks it through
  `ClearTree`. Measured (2,000 documents, 16-page threshold, no extension):
  one insert with a vector index 63 pages, dropping the vector index 56.
  "Per-document checks need no change" was wrong for vector-backed writes.
  Safepoints go at traversal boundaries with node reload; a streaming
  `Search()` is not enough because its traversal and document loading are
  eager.
- **Loading waits use `_sync` itself** (5.2): `Monitor.Wait(_sync)` /
  `PulseAll(_sync)`, and a woken waiter re-resolves its key in the dictionary
  before pinning, because the frame it waited on may have been published,
  released and reused for another key. No second lock, no lock ordering.
- **The WAL fix uses the frame `MoveToReadable` returns** (5.2). Today
  `WriteLogDisk` ignores that return value and writes and releases the
  original page; in the duplicate-key branch the original is already
  discarded (reproduced: `ArgumentOutOfRangeException`, original free,
  returned frame still pinned). Write, callback and release now all use the
  returned frame, with ownership cleanup when either throws.
- **Lifetime checks cover slices, not only `BasePage`** (5.2, 6):
  `IndexNode.SetNextNode` writes and `VectorIndexNode.GetNeighbors` /
  `ReadVector` read through retained `BufferSlice`s. Segment liveness is
  restated: a zero pin count removes a frame from the pool's accounting; it
  does not make the array collectable while a suspended iterator still holds
  a node. Reported and tested separately.
- **CLOCK has an explicit work bound and trim prioritizes releasable
  segments** (5.2): frames examined per acquisition are capped and measured,
  segment selection uses occupancy buckets, and trim evicts from segments
  that can become fully free before touching others.
- **`Query.In` keeps value-snapshot semantics** (5.8): parameterized helpers
  clone mutable inputs (array, document, binary) at construction, because
  today's text serialization snapshots them and a retained parameter would
  not (reproduced). Cache admission is atomic under a lock.
- Smaller: the overshoot formula includes one collection page per snapshot;
  stream ownership covers the log and sort streams the engine creates for a
  caller-supplied `DataStream`; the summary and RC7 now say what section 5
  supports.

### Revision 3 (second review)

The architecture and the benchmark-driven defaults stand. Seven refinements,
five of them measured or reproduced against the Release build:

- **The safepoint audit was incomplete.** With a 16-page threshold and
  extension disabled, ordinary workloads still pinned far more: non-indexed
  `OrderBy` 1,202 pages, one document with an `Include` array 1,266, indexed
  `LIKE` with no matches 175, `DropIndex` 212 (6,000-document harness). 5.3
  now lists the mechanisms and the required safepoints; no scan-memory bound
  is claimed until the regression tests for those paths exist.
- **Frame ownership extends to WAL callers.** `DiskService.WriteLogDisk`
  releases a written page before resuming its source iterator, and that
  iterator then reads `buffer.Position` to fill `DirtyPages`
  (`TransactionService.cs:199`). Reproduced: zero pins on resumption, the
  frame reused by another thread, position changed. Phase 1 records the page
  id and WAL position before the release; the transition table now shows that
  publication happens before the disk write, under the writer's pin.
- **CLOCK reports two outcomes.** A full turn over idle frames whose reference
  bits are all set evicts nothing; acquisition and trim now run a bounded
  second pass and distinguish "cleared bits" from "nothing eligible".
- **`CacheSize` is a soft target on idle frames, not a bound on the engine.**
  One pinned frame retains its whole 1 MB segment, so scattered pins can
  retain far more than they hold; 100 open transactions × 1,000 pages is
  still 800 MB at the hard maximum. 5.2 accounts pinned, writable, loading
  and segment-retained bytes separately, and the design no longer promises a
  bound on transaction metadata or query-side collections.
- **Synchronization contract tightened.** Every `ShareCounter` change is
  atomic (a plain `++` under the lock races with the lock-free decrement);
  waiters loop on the frame's monitor; `TryMoveToReadable`, writable-load
  failure and a writable request meeting a `Loading` entry are specified.
- **Parameter renumbering is collision-free.** Offsetting numeric names by
  `left.Parameters.Count` fails for sparse numbers and for named parameters
  (`@value` on both sides). 5.8 assigns fresh positional names to every
  referenced parameter in both operands.
- **Rollout no longer claims bounded growth before enforcement exists.**
  Phase 3 is settings plus reduced transaction retention plus the safepoint
  audit; capacity enforcement and segment reclamation are phase 4; stream
  ownership fixes are an independently reviewable change.

### Revision 2 (first review)

Revision 1 was reviewed against the codebase and reproduced in three places
where it was wrong. This revision changes the design, not the diagnosis:

- **Checkpoint invalidation stays separate from trimming** (5.2). Revision 1
  replaced `MemoryCache.Clear()` with a trim, which would have left stale data
  pages and stale log positions in a cache that was under its limit.
- **Frames get explicit states and one lock covers every transition** (5.2).
  The bare compare-and-swap protocol of revision 1 still allowed a paused
  reader to pin a frame that had been evicted and reused (0 → −2 → 0), and it
  did not cover loading, publication, or the writable-copy read.
- **Lost frames on concurrent or failed loads are a first-phase fix** (5.2).
  Today's `GetOrAdd` factory can run twice for one key; the loser's frame and
  the frame of a failed load are never returned. Reproduced: two simultaneous
  reads of one key left one frame unaccounted for.
- **Transaction policy is a fixed per-transaction safepoint threshold** (5.3),
  not an extensible shared budget; revision 1's formula let a lone transaction
  reach 6,000 pages while claiming 1,000. Cooperative-safepoint overshoot is
  now documented and audited.
- **Query composition must carry parameters** (5.8). `Query.And`/`Or`
  concatenate source text; parameterizing the leaf helpers alone silently
  drops parameters. Reproduced: a literal conjunction was true, its
  parameterized equivalent false. The cache cap ships first, on its own.
- **Trim has termination and rounding rules** (5.2, 6).
- **Stream claims corrected** (5.5, 5.6): the wrong-password path already
  disposes the file; log shrinking must go through the stream abstraction
  because the encryption wrapper keeps a header page.
- **Rollout reordered** (7): correctness of loading and eviction first, then
  the expression cache, then settings and the transaction policy, then
  segments, CLOCK and trimming, then defaults from benchmarks. Revision 1's
  "low-risk patch" raised eviction frequency before fixing eviction.
- **CI gap** (6): `LiteDB.Tests.csproj` removes `Internals\**` in Release, so
  the cache tests do not run in a Release test pass today.

## 1. Summary

Every "LiteDB eats memory" report against the v5 engine that is not a user-side
`ToList()` reduces to four engine properties: three in the page cache and the
transaction page budget, one in a static expression cache:

1. **The cache never shrinks.** `MemoryCache` only ever allocates segments; there
   is no code path that releases a segment. Peak working set becomes permanent
   working set until the `LiteDatabase` is disposed.
2. **A single transaction may pin up to 100,000 pages (800 MB) before it
   releases anything.** `MAX_TRANSACTION_SIZE` is a global budget that one
   transaction can absorb in 1,000-page steps, and `Safepoint()` only fires when
   the budget is exhausted. Pinned pages cannot be recycled, so the cache must
   grow to hold them. A full scan of any database therefore drives the cache to
   `min(database size, ~800 MB)`, after which property 1 keeps it there.
3. **The recycle heuristic has hysteresis in the wrong direction.** Pages are
   reused only when more than *next segment size* (1,000) idle readable pages
   exist; otherwise 8 MB is allocated. Workloads whose idle readable count
   hovers below 1,000 allocate on every dip and never reuse.
4. **A process-wide static cache of compiled expressions grows forever.**
   `BsonExpression` caches compiled delegates in two static dictionaries keyed
   by expression *source text*. `Query.EQ("Name", value)` and its siblings
   embed the value into that text, so every distinct value adds a permanent
   entry (2.5 GB after five days in #1688). This survives `Dispose()`.

There is no post-dispose leak in the engine: with a forced full GC the engine, its cache, and
all segments are reclaimed after `Dispose()` (verified with weak references).
Users who report "memory is not returned" are observing a live engine.

The fix proposed here gives the page pool a **soft target** (configurable,
default 64 MB for file databases; idle frames never push it past the target,
pinned frames and partially occupied segments can and are reported), makes it
**elastic** (segments are returned to the GC after a peak once their frames
are free), and makes pins **accountable** (a fixed per-transaction safepoint
threshold with every cooperative-safepoint gap audited and tested). It does
not bound the whole engine heap; section 4 says exactly what it bounds. It keeps the on-disk
format, the public API, and the page-pinning protocol, and it removes one
existing "no way to fix" exception path in the cache.

## 2. Measurements (this fork, Release, net8.0, 1 KB documents)

Repro program: appendix A. `cache` columns
come from `SELECT $ FROM $database`. `heap` is `GC.GetTotalMemory(true)`.

### 2.1 Full scan of a 279 MB file (200,000 docs)

| Step | heap | cache segments | cache pages | cache MB | pinned |
|---|---|---|---|---|---|
| opened | 0 MB | 1 | 12 | 0 | 0 |
| `Count()` | 13 MB | 5 | 1,662 | 12 | 0 |
| `FindAll()` streamed once | 275 MB | 38 | 34,662 | 270 | 0 |
| `FindAll()` streamed twice | 275 MB | 38 | 34,662 | 270 | 0 |
| index range `age < 10` | 275 MB | 38 | 34,662 | 270 | 0 |
| `Checkpoint()` | unchanged | | | | |

The cache equals the file size after one scan and never drops. This is
issue #2278 verbatim ("queries pull entire DB size into RAM and leave it
there after done").

### 2.2 Full scan of a 1,256 MB file (900,000 docs)

| Step | heap | cache segments | cache pages | cache MB |
|---|---|---|---|---|
| `Count()` | 45 MB | 9 | 5,662 | 44 |
| `FindAll()` streamed once | 798 MB | 104 | 100,662 | 786 |
| `FindAll()` streamed twice | 806 MB | 105 | 101,662 | 794 |

The cache stops at `MAX_TRANSACTION_SIZE` (100,000) plus one segment: the scan
transaction absorbed the entire global budget before its first `Safepoint()`.
That is the ~800 MB / ~1 GB ceiling users report in #2311, #2395, #2619.

### 2.3 Half-consumed cursor left open (200,000 docs)

| Step | cache pages | pinned (`pagesInUse`) | tx budget available |
|---|---|---|---|
| opened | 12 | 0 | 99,000 |
| cursor half consumed, still open | 17,662 | 17,273 | 81,000 |
| cursor disposed | 17,662 | 0 | 99,000 |

One query transaction extended its budget 19 times and pinned 135 MB.

### 2.4 Bulk write, 200,000 inserts in 1,000-doc batches with periodic updates and deletes

| Step | cache segments | cache pages | free | readable | writable |
|---|---|---|---|---|---|
| 50,000 inserted | 5 | 1,662 | 715 | 947 | 0 |
| 200,000 inserted | 9 | 5,662 | 5,009 | 653 | 0 |
| after `Checkpoint()` | 9 | 5,662 | 5,662 | 0 | 0 |

The working set never exceeded ~1,600 pages, yet 44 MB was allocated: during a
batch the working set is *writable* pages (not in `_readable`), idle readable
pages stay below the 1,000-page recycle threshold, so every dip allocates a
new 8 MB segment. Those pages then sit in `_free` forever. This is the
mechanism behind #1756 ("free queue grows unbounded").

### 2.5 Steady-state point lookups (100,000 `FindById` over 200,000 docs)

Cache stays at 5 segments / 1,662 pages / 12 MB. When the idle readable count
exceeds 1,000 the recycle path works; the problem is only the pinned case and
the hysteresis case.

### 2.6 `:memory:` database, 100,000 docs (~95 MB raw)

| Step | heap | cache pages | cache MB |
|---|---|---|---|
| inserted | 229 MB | 2,662 | 20 |
| `FindAll()` streamed | 348 MB | 17,662 | 137 |
| `Checkpoint()` | 348 MB | 17,662 | 137 |

The data `MemoryStream` (with doubling growth), the log `MemoryStream` (whose
capacity survives `SetLength(0)` at checkpoint) and the page cache each hold a
copy. ~3.6x the raw data. This is #2541.

### 2.7 300 open databases

35 MB heap, ~117 KB per engine (one eagerly allocated 12-page segment plus
streams). #1479.

### 2.8 Dispose

`WeakReference` on `LiteDatabase` and `LiteEngine` after `Dispose()` + full GC:
both dead, heap back to 0 MB. Repeated on a worker thread (ThreadLocal slot in
`TransactionMonitor`): both dead. No engine-level leak after dispose.

## 3. Root causes in the code

### RC1 — no release path (`MemoryCache.cs`)

`Extend()` allocates `new byte[PAGE_SIZE * segmentSize]` and enqueues slices
into `_free`. Nothing ever removes a `PageBuffer` from `_free`/`_readable`
except to hand it out again. `Clear()` (checkpoint) moves everything to
`_free`. `Dispose()` is empty. Segment sizes ramp `12, 50, 100, 500, 1000,
1000, ...` so steady-state growth is 8 MB per extend.

### RC2 — transaction budget is a memory ceiling, reachable by one reader

- `Snapshot.GetPage()` pins every page it touches (`ShareCounter++` for
  readable pages, or a private writable copy) and keeps it in `_localPages`
  until `Clear()`/`Dispose()`.
- `TransactionService.Safepoint()` releases only when
  `TransactionMonitor.CheckSafepoint()` is true, i.e. when the transaction is
  at its `MaxTransactionSize` *and* `TryExtend()` fails.
- `TryExtend()` succeeds while the global `_freePages` (starts at 100,000)
  has 1,000 left. One transaction therefore climbs to ~100,000 pinned pages.
- `Extend()` can only recycle pages with `ShareCounter == 0`; pinned pages
  force allocation. Cache size ≥ pinned pages always holds.

Net: the intended "100,000 pages ≈ 1 GB shared across all transactions" is in
practice the memory *floor* after any large scan, not a rarely reached cap.

The fallback in `GetInitialSize()` when the budget is exhausted reduces every
open transaction by `MaxTransactionSize / _initialSize` pages (1 page for a
1,000-page transaction) and hands the sum to the newcomer; the code carries a
`//TODO: revisar estas contas` comment. Transactions created in that state
safepoint on nearly every page.

### RC3 — recycle hysteresis (`Extend()`)

```csharp
var emptyShareCounter = _readable.Values.Count(x => x.ShareCounter == 0);
var segmentSize = _segmentSizes[Math.Min(_segmentSizes.Length - 1, _extends)];
if (emptyShareCounter > segmentSize) { /* recycle segmentSize oldest */ }
else { /* allocate segmentSize new pages */ }
```

The reuse decision is tied to the size of the *next* allocation. With ≤1,000
idle readable pages the cache prefers to allocate 8 MB over reusing 999
pages. Both branches also enumerate and (in the recycle branch) LINQ-sort
the whole `_readable` dictionary under a lock: O(n log n) per extend with n
up to 100,000.

`MoveToReadable` never refreshes `Timestamp`, so a page that was just
written keeps the age it had as a writable copy and is recycled *first* by
the timestamp sort: the hottest pages go before colder ones read later.
CLOCK's reference bit removes that inversion too.

### RC4 — the eviction race has no correct resolution

`Extend()` removes an idle page from `_readable`, then discovers a reader
pinned it in the meantime and tries to put it back; if a concurrent
`GetReadablePage` already re-created the key it throws
`"MemoryCache: removed in-use memory page. This situation has no way to fix
(yet)"`. `GetReadablePage` increments `ShareCounter` unconditionally, so
there is no way for it to notice an eviction in progress.

### RC5 — `:memory:` / stream-backed databases store pages twice, log capacity is never released

`StreamFactory` wraps the user's `MemoryStream`; every page read is copied into
the cache. `DiskService.SetLength(0, Log)` on a `MemoryStream` keeps the
buffer capacity. Streams created by LiteDB itself for `:memory:` and `:temp:`
have `CloseOnDispose == false`, so a `TempStream` that spilled to disk is never
disposed (#2056).

### RC6 — fixed per-engine baseline

The first 12-page segment is allocated in the `MemoryCache` constructor
(comment: to land on the LOH). Per open engine that is ~100 KB before any
page is read (#1479).

### RC7 — stream disposal and ownership on failure

The wrong-password path is covered: `AesStream`'s constructor disposes the
underlying `FileStream` in its `catch`. What is not covered:
`File.SetAttributes` for hidden files throwing after the `FileStream` exists
in `FileStreamFactory.GetStream()`, the `DiskService` constructor failing
after it created pools (#2614), and the log and sort streams the engine
creates itself (for `:memory:`, `:temp:`, and for a caller-supplied
`DataStream` with no `LogStream`) never being disposed because
`StreamFactory.CloseOnDispose` is false (#2056). #2579 as reported should be
re-verified against 5.0.21.

### RC8 — static compiled-expression cache keyed by source text (`BsonExpression.cs`)

```csharp
private static readonly ConcurrentDictionary<string, BsonExpressionEnumerableDelegate> _cacheEnumerable = ...;
private static readonly ConcurrentDictionary<string, BsonExpressionScalarDelegate> _cacheScalar = ...;
// ...
var cached = _cacheScalar.GetOrAdd(expr.Source, s => ...compile...);
```

and in `Client/Structures/Query.cs`:

```csharp
public static BsonExpression EQ(string field, BsonValue value)
    => BsonExpression.Create($"{field} = {value ?? BsonValue.Null}");
```

Each distinct literal produces a distinct `Source`, hence a distinct compiled
delegate that is never evicted. The LINQ path is unaffected because the
visitor emits parameters. Reported as #1688 (closed by the reporter with a
reflection-based periodic clear), #2421 (open), and, by inference from the
discriminating call (`DeleteMany(Query.EQ("Name", uniqueName))` grows,
`DeleteMany(x => x.Id == id)` does not), #2395.

### Not engine bugs, but recurring in reports

- `FindAll().ToList()` / `ToArray()` on large collections (#2139, #2289,
  #2400): the documents are the user's, but the engine's cache growth (RC1,
  RC2) makes the "database memory" look like a leak on top of the list.
- Long-lived `LiteDatabase` singletons in services (#2311, #2388): correct
  usage; they simply expose RC1.

## 4. Design goals

| Goal | Concrete meaning |
|---|---|
| Soft target | `CacheSize` is a target for the page buffer pool. Default 64 MB. Idle frames never push allocated bytes past it; pinned, writable and loading frames can, and so can partially occupied segments, and both are reported separately. It is not a bound on the engine heap: transaction metadata, sort containers, query-side collections and materialized documents are outside the pool. |
| Elastic | After a peak the cache returns segments to the GC down to the limit, without timers. |
| Pin-honest | The overshoot has a stated formula: open transactions × the safepoint threshold, plus the audited per-path overshoot, plus segment retention by scattered pins. At the hard maximum of 100 open transactions that is still 800 MB; typical apps run one to four. |
| Cheap | O(1) amortized page acquisition and eviction; no LINQ over the whole cache; no sort. |
| Race-free | Every frame transition is explicit and covered by one lock; loading, publication, eviction and the writable copy included. The "no way to fix" throw disappears. |
| Observable | `$database.cache` reports limit, allocated bytes, pinned pages, evictions, segment releases, hit/miss counts. |
| Compatible | No file-format change, no public API removal, existing `PageBuffer`/`ShareCounter` semantics preserved for callers. |

Non-goals: a user-space replacement for the OS page cache (the cache exists to
avoid syscalls, copies and AES decryption, not to hold the database), and
changing the WAL/checkpoint design.

## 5. Proposed design

### 5.1 Configuration

- `EngineSettings.CacheSize` (`long`, bytes). Default `64 MB` for file
  databases, `8 MB` when `DataStream` is a `MemoryStream` or `Filename` is
  `:memory:` (the "disk" is already RAM). Minimum enforced: 2 segments.
- `ConnectionString`: `cache size=64MB` (parsed with the same `ParseFileSize`
  used by `initial size`). Note for v4 migrants: v4's `cache size` was a page
  count; a bare number is now bytes and will clamp to the minimum. The
  parser should reject values without a unit below 1 MB with a clear message
  instead of silently clamping.
- Internal: `LimitPages = CacheSize / PAGE_SIZE`, rounded up to whole
  segments (5.2).
- `EngineSettings.TransactionPageLimit` (pages, default 1,000) and connection
  string `transaction pages=1000`: the per-transaction safepoint threshold
  (5.3).

### 5.2 `MemoryCache` v2: explicit frame states, one lock, segments, CLOCK

Replace the `ConcurrentQueue<PageBuffer>` free list, the `ConcurrentDictionary`
publication and the timestamp-sort recycle with a buffer pool whose every
transition is explicit and covered by one synchronization design. Contention
is optimized only after it is measured; correctness comes first.

```
Segment
  byte[]        Buffer          // 1 MB (128 pages) after the first segment
  PageBuffer[]  Frames          // fixed, never re-created
  int           FreeCount
  int           FreeHead        // intrusive free list through PageBuffer.NextFree
  int           Busy            // frames pinned, Writable or Loading; 0 ⇒ releasable once free

PageBuffer (additions)
  FrameState    State           // Free | Loading | Readable | Writable
  int           ShareCounter    // pin count while Readable (kept for callers)
  long          Generation      // incremented every time the frame becomes Free
  int           Referenced      // CLOCK reference bit
  Segment       Segment
  int           NextFree
```

**Synchronization contract.** A single `_sync` lock protects the readable
index (`Dictionary<long, PageBuffer>`), every frame state transition, the
segment list and the free lists. Page *loading* (the disk read into the
frame) runs outside the lock. Rules:

- Every change to `ShareCounter` happens under `_sync`, `Release()`
  included. Today `Release()` is a lock-free `Interlocked.Decrement`
  (`PageBuffer.cs:58`) and nothing else needs to know; in this design the
  segment's `Busy` count and its occupancy bucket must learn when the last
  pin disappears, and a decrement outside the lock cannot update them. The
  last-release protocol: `Release()` takes `_sync`, decrements, and on
  `1 → 0` decrements the segment's `Busy` count and moves the segment
  between buckets; a reader pinning `0 → 1` under the same lock does the
  reverse, so a concurrent re-pin and a segment release cannot interleave.
  The lock-free variant (decrement outside, lock only on `1 → 0`, re-read
  the count under the lock because a pin can arrive in between) is an
  optimization the concurrent-reader benchmark in section 6 has to justify;
  phase 1 ships the simple form.
- Waiters for a `Loading` frame call `Monitor.Wait(_sync)` inside the lock
  they already hold; the loader takes `_sync` and calls
  `Monitor.PulseAll(_sync)` after publishing `Readable` or `Free`. One lock,
  no ordering to define, and check-and-wait is atomic. A woken waiter does
  not trust the frame it waited on: the loader may have published, released
  and let that frame be evicted and reused for another key. The wait is a
  loop on the key, not on the frame:

      while (_index.TryGetValue(key, out frame) && frame.State == Loading)
          Monitor.Wait(_sync);
      if (frame != null && frame.State == Readable) Pin(frame); else Load(key);

  Because the key is re-resolved on every iteration no generation is needed
  in the wait condition: a frame that failed and was reused for another key
  is simply no longer the frame the key maps to. A per-load completion
  object can replace the shared condition later if contention is measured.
- A writable request (`GetWritablePage`) that finds a `Loading` entry waits
  like a reader, then copies; one that finds nothing loads directly into its
  own writable frame, and on exception that frame returns to `Free` (there is
  no index entry to remove).
- `TryMoveToReadable` (clean pages after a write transaction) transitions
  `Writable → Readable` only if the key is absent; otherwise the frame stays
  `Writable` and the caller discards it, as today.

This removes the three races found in review:

- **ABA on pin.** A frame can only leave the readable index inside the same
  critical section that changes its state, so a reader that finds a frame in
  the index under the lock cannot be looking at a frame that was evicted and
  reused. `Generation` is additionally recorded by `BasePage` in
  `DEBUG || TESTING` and asserted on its own accesses; node types read and
  write through retained slices without touching `BasePage`, so for them the
  slice-level checks and the `0xFF` poison below are the detectors. Any
  future regression surfaces as an `ENSURE`, not as silent wrong data.
- **Lost frames on concurrent load.** Publication is `TryAdd` of a frame in
  `Loading` state, not a `GetOrAdd` factory. The loser of the race returns its
  frame to its segment before waiting on the winner's frame.
- **Lost frames on failed load.** The loader owns the frame until it publishes
  `Readable`; on exception it removes the index entry, sets `Free`, pulses the
  waiters (which retry and will load themselves), and returns the frame.

**Transitions** (all under `_sync` unless noted):

| From | To | When | Who |
|---|---|---|---|
| Free | Loading | `GetReadablePage` miss: frame taken from a segment, `Position/Origin` set, entry added to the index | reader |
| Loading | Readable | disk read finished (outside the lock), then `ShareCounter = 1` for the loader, `PulseAll` | loader |
| Loading | Free | disk read threw; index entry removed, `PulseAll`, frame returned | loader |
| Readable | Readable (+1 pin) | `GetReadablePage` hit: `ShareCounter++`, `Referenced = 1` | reader |
| Readable | Free | CLOCK sweep (one atomic transition, no observable intermediate state): `ShareCounter == 0 && Referenced == 0`; index entry removed, `Generation++`, frame returned to its segment | evictor |
| Readable | Free | `Invalidate()` after checkpoint (requires the engine's exclusive lock; every `ShareCounter` must be 0) | checkpoint |
| Free | Writable | `NewPage`, `GetWritablePage` | transaction |
| Writable | Readable (pinned by the writer) | `MoveToReadable` *before* the log write: the frame is published so new readers see the content, held by the writer's pin while the bytes go to disk, and released after the write. The key must be absent; a collision is an invariant failure (`ENSURE`), not an overwrite (see below) | transaction |
| Writable | Writable | `TryMoveToReadable` when the key already exists: no transition, caller discards | transaction |
| Writable | Free | `DiscardPage` (rollback / clean page) | transaction |

`GetWritablePage` copies from the readable source *under the lock* (an 8 KB
copy) so the source cannot be evicted or overwritten mid-copy; today it copies
without pinning (`MemoryCache.cs:112`).

**Ownership beyond `MemoryCache`.** `DiskService.WriteLogDisk` assigns the
WAL position, publishes, writes, then calls `page.Release()` and only then
resumes the source iterator; `TransactionService.PersistDirtyPages` resumes
after the `yield` and reads `buffer.Position` to fill `DirtyPages`
(`TransactionService.cs:199`). Between the release and that read another
thread can recycle the frame (reproduced: zero pins on resumption, frame
reused, position changed to `long.MaxValue`). There is a second gap in the same method: it ignores the frame that
`MoveToReadable` returns (`var readable = _cache.MoveToReadable(page);`) and
keeps writing from and releasing the original `page`. In the duplicate-key
branch the original is already discarded to the free list by then (reproduced
by forcing that branch: `ArgumentOutOfRangeException`, original frame free,
returned frame still pinned). That branch is unreachable in the shipped
engine: `WriteLogDisk` is the only caller and assigns every page a fresh
position (`Interlocked.Add(ref _logLength, PAGE_SIZE)`), and the only log
truncation, `WalIndexService.Clear()`, empties the cache before
`SetLength(0, Log)` under the exclusive lock. The new cache therefore treats
a key collision in `MoveToReadable` as an invariant failure (`ENSURE`), and
the "existing frame must be idle, overwrite its bytes" branch is deleted
rather than carried into the transition table with a condition nobody can
guarantee.

Phase 1 changes the contract:
`WriteLogDisk(IEnumerable<PageBuffer>, Action<uint pageID, long position>
written)` writes from the frame `MoveToReadable` returned (after the change
above, always the frame it was given), invokes the callback with that
frame's position after the write, then releases that frame. If the write
or the callback throws, the frame is still released (it is published and
readable; the transaction is failing and will not confirm it) and the
exception propagates. `ReturnNewPages` (rollback) has the identical pattern
and gets the same change. Failure-injection tests cover both throw points.

**Acquire a free frame** (inside `_sync`):

1. Pop from the current segment; when it has no free frame left, take the
   most populated segment with a free frame from the occupancy buckets and
   make it current. Nearly-empty segments drain and become releasable, and
   the bucket lookup happens once per segment, not once per acquisition.
2. None free and `TotalPages < LimitPagesRounded`: allocate a segment (1 MB).
3. Otherwise, if the maintained `idleReadable` count is zero (every frame is
   pinned, writable or loading), no victim exists: allocate anyway and count
   `overflowSegments`. 5.3 bounds how often this happens.
4. Otherwise a victim exists: run the CLOCK sweep until it evicts (up to 128
   frames in one go), then retry step 1. The sweep never allocates.

**CLOCK sweep**: a hand walks the frames. `Readable && ShareCounter == 0`:
`Referenced == 1` → clear it and move on; else evict. Every other state is
skipped. A turn returns two counts, `evicted` and `clearedBits`. A cache of
idle frames whose bits are all set evicts nothing on the first turn, so both
acquisition and trim run a bounded second turn when `evicted == 0 &&
clearedBits > 0`; only `evicted == 0 && clearedBits == 0` means "nothing
eligible". No sort, no allocation, no LINQ.

**Work bound.** Two full turns do not make acquisition O(1): with many pinned
frames and few eligible ones every miss could rescan the pool. The bound is
measured, not bought with allocation: `EvictScanBudget` (default 2 × 128 =
256) is the number of frames an acquisition is *expected* to examine; the
hand position persists between calls so consecutive misses continue the
sweep instead of restarting it; `framesExamined` and `budgetExceeded`
(acquisitions that examined more than the budget) are exposed in
`$database` and tracked in the benchmarks. Running out of budget does not
allocate. Revision 4 allowed that, and it broke the idle guarantee: a 64 MB
pool of 8,200 idle frames whose reference bits are all set would have
cleared 256 bits, exhausted the budget and grown past the target with no pin
anywhere. Step 3 decides whether a victim exists from the `idleReadable`
count in O(1); when one exists the sweep continues until it finds one. The
worst case is one turn over the pool that clears bits plus the distance to
the next idle frame, bounded by twice the pool. If the benchmarks show that
case matters, per-segment idle counts let the hand skip pinned-heavy
segments; that is applied after measuring, not before.

**Segment selection** does not scan all segments: segments sit in occupancy
buckets (by free-frame count: 0, 1–15, 16–63, 64–127, 128), maintained on
every free/take, so "most populated segment with a free frame" and "fully
free segments" are O(1) lookups.

**Invalidate** (checkpoint) is unchanged in meaning and stays a separate
operation: under the engine's exclusive lock, every readable frame becomes
Free regardless of the limit, because the checkpoint rewrote data pages and
truncated the log, so both cached data contents and cached log positions are
stale. Invalidation releases no segment by itself; only segments beyond
`LimitPagesRounded` are released afterwards, through the same
`ReleaseFullyFreeSegments(downTo: LimitPagesRounded)` step the trim uses.
Auto-checkpoint runs every `CHECKPOINT` pragma pages (default 1,000 = 8 MB
of log, `EnginePragmas.cs:60`): releasing every fully free segment there
would drop and re-allocate up to 64 one-megabyte arrays per 8 MB written,
gen2 and LOH churn the current code never causes. Memory within the limit is
the contract, not a leak. Revision 1 wrongly folded invalidation into the
trim; revision 4 wrongly made it a release point.

**Trim** (`TrimToLimit()`), called from `TransactionMonitor.ReleaseTransaction()`:

```
while (TotalPages > LimitPagesRounded)
{
    var (evicted, cleared) = ClockTurn(maxEvict: TotalPages - LimitPagesRounded);
    if (evicted == 0 && cleared > 0) (evicted, cleared) = ClockTurn(...);   // second chance pass
    ReleaseFullyFreeSegments(downTo: LimitPagesRounded, keepSpare: 1);
    if (evicted == 0) break;          // no progress: remaining frames are pinned/writable/loading
}
```

Termination is explicit: when every remaining segment holds a pinned or
writable frame the trim stops and the overshoot is reported, it does not
spin. Trim also chooses its victims by segment, not by hand position: it
takes segments from the "no pinned, writable or loading frames" bucket first
(the only ones that can become fully free) and evicts their idle readable
frames; evicting idle pages out of segments that a scattered pin keeps alive
loses useful cache content and releases nothing, so those are touched only
when no releasable segment remains and the pool is still over target. Transaction end in one thread says nothing about other transactions'
pins; the next transaction end will try again.

**Segment rounding.** Allocation is in whole segments, so the enforceable
limit is `LimitPagesRounded = firstSegmentPages + ceil((LimitPages −
firstSegmentPages) / 128) × 128`; with an 8-page first segment and a 64 MB
limit that is 8,200 pages, not 8,192. Every bound in this document and every
test asserts against the rounded value.

**Lifetime checks beyond `BasePage`.** Node types keep a `BufferSlice`
into the frame and use it directly: `IndexNode.SetNextNode`/`SetNext`/`SetPrev`
write through `_segment`, `VectorIndexNode.GetNeighbors` and `ReadVector` read
through it. A generation check alone is not enough for them. A safepoint
(`TransactionService.Safepoint`) writes the dirty pages to the log, which
moves their frames from `Writable` to `Readable`, discards the clean
writable copies, and clears every snapshot; no frame becomes `Free`, so
nothing bumps a generation. Reproduced in review: after a safepoint a
retained `IndexNode` pointed at a readable frame with zero pins and a valid
position, its snapshot tracked zero pages, and `SetNextNode` still accepted
the write, into a frame that other readers now share and that eviction may
recycle at any moment. The rule is ownership: a page-backed object is valid
only while the snapshot that produced it still tracks the page. In `DEBUG ||
TESTING` the slice carries the frame, the generation, and the snapshot epoch
(`Snapshot.Clear()` increments it) captured at construction; every read
asserts generation and epoch, and every write additionally asserts
`State == Writable`. A stale node then fails on the exact access, whether
the frame was evicted, invalidated, or merely handed to the readers by a
safepoint. Release builds keep the `0xFF` poison on freed frames as the
cheap detector.

**Release a segment** when its `FreeCount == Frames.Length`, the pool is
above `LimitPagesRounded`, more than one spare fully-free segment exists,
and it is not the first segment: unlink it and drop the `byte[]`. Precisely: a free frame is out of the pool's
*accounting*, its generation is bumped and its index entry removed, so the
pool will never hand its old content to anyone. That is not the same as the
array being collectable: a suspended iterator (an abandoned cursor, a
`foreach` that stopped early) can still hold an `IndexNode` whose slice
points into the segment after a safepoint released the pin. The released
segment then stays alive until that iterator is collected. The cache reports
`releasedSegments` (dropped from accounting) and the tests measure
collectability separately with weak references, including a case with a
suspended cursor that must *not* be collectable until the cursor is
dropped.

**Fragmentation, stated plainly.** One pinned, writable or loading frame
keeps its whole 128-page segment allocated. 100 pinned frames scattered over
100 segments retain ~100 MB for under 1 MB of content. Allocating into the
most populated segment improves future packing but cannot repair existing
scatter; only the pins ending can. The cache therefore reports
`retainedBySegments` (frames in partially occupied segments that are free but
unreleasable) next to `pinnedPages`, `writablePages` and `loadingPages`, and
`CacheSize` is documented as a soft target on idle frames (section 4). The
scattered-pin test in section 6 pins one frame per segment and asserts the
reported retention, not a release.

**Counters** (`Interlocked` or under the lock) replace the LINQ properties
`PagesInUse`, `WritablePages`, `FreePages`, and `WritablePages` becomes a
maintained count instead of `ExtendPages − free − readable`, which today
mis-reports lost frames as writable.

### 5.3 Transaction policy: a fixed per-transaction safepoint threshold

Revision 1 proposed `PerTxInitial = 1,000` with extension while a global
budget had room, and claimed a scan releases every 1,000 pages. It does not:
with a 6,144-page global budget a lone transaction extends to 6,000 pages
before its first safepoint. The two options are an extensible shared budget
(today's design, with a smaller pool) or a fixed per-transaction threshold.
This proposal chooses the fixed threshold:

- `EngineSettings.TransactionPageLimit` (default 1,000 pages = 8 MB;
  `TransactionService.MaxTransactionSize` keeps its name and setter so the
  existing tests still work). `Safepoint()` fires when
  `TransactionSize >= MaxTransactionSize`. `TransactionMonitor.TryExtend` and
  `GetInitialSize` are removed; `MAX_OPEN_TRANSACTIONS` stays.
- Today a transaction that never extended already behaves this way (initial
  quota 1,000), so the write-path cost is known: one log flush per 8 MB of
  dirty pages.
- Pinned pages are bounded by `open transactions × (TransactionPageLimit +
  snapshots per transaction) + overshoot`: the collection page of every
  snapshot survives `Snapshot.Clear()` and is released only at transaction
  end. With the defaults and four concurrent single-collection transactions
  that is 32 MB plus four pages plus overshoot, inside a 64 MB target. The
  cache reports `overflowSegments` when pins force it past the rounded
  limit.

**Overshoot.** Safepoints are cooperative, so the threshold is a target the
engine checks at specific points, not a hard cap. A Release build with the
threshold forced to 16 pages and extension disabled (6,000 documents)
measured the maximum pages one transaction held:

| Operation | Max transaction pages | Why |
|---|---|---|
| Streamed scan | 16 | pipeline check per document works |
| Non-indexed `OrderBy` | 1,202 | sort input is checked, but the sorted output reloads documents through `_lookup.Load` with no check (`BasePipe.cs:177` and `:210`) |
| One document with an `Include` array | 1,266 | `Include` loads each referenced document directly, no check per reference (`BasePipe.cs:117`) |
| Indexed `LIKE`, no matches | 175 | `IndexLike.ExecuteLike` filters on the index side (`FindAll(...).Where(...)`), so a non-matching walk never yields a node and the pipeline safepoint is never reached (`IndexLike.cs:117`) |
| `DropIndex` | 212 | `IndexService.DropIndex` walks every PK node and its chain with no callback (`IndexService.cs:300`) |
| `Delete` over 6,000 absent ids | 111 | an absent id runs `continue` before the safepoint (`Delete.cs:38`); the PK seeks pin pages that nothing releases |
| Indexed `IN` over 6,000 absent values | 111 | each seek that misses yields nothing (`IndexIn.cs:30`), so the pipeline check never runs |

The audit below lists every place a transaction can touch pages between
checks, the fix each needs, and the regression test that must exist before
any bound is claimed for that path. Until every row has its test, the design
claims no bound tighter than "threshold plus the largest single step in this
table".

| Path | Today | Required change | Test |
|---|---|---|---|
| Query pipeline, streamed | check per yielded document | none | `Scan_PinsAtMostThresholdPlusOneDocument` |
| Sorted output (`OrderBy`/`GroupBy` without index) | input checked; sorted reload unchecked | safepoint per reloaded document in `QueryPipe`/`GroupByPipe` after the sort | `OrderBy_NoIndex_PinsBounded` |
| `Include` | unchecked per reference | safepoint per included document (and per array item) in `BasePipe.Include` | `Include_Array_PinsBounded` |
| Index-side filters (`IndexLike.ExecuteLike`; `IndexScan`, which the optimizer uses for not-equal, `IndexCost.cs:92`; any `Where` inside an `Index.Execute`) | no yield → no check | safepoint per visited node inside the index enumerators, through the snapshot (wiring below) | `Like_NoMatch_PinsBounded`, `NotEqual_NoMatch_PinsBounded` |
| Index seeks that miss (`IndexIn`, and `IndexEquals` behind it) | a missed seek yields nothing, so no downstream check runs | safepoint per attempted value inside `IndexIn.Execute` | `IndexIn_AbsentValues_PinsBounded` |
| Aggregate replay through `DocumentCacheEnumerable` | replay reloads without check | safepoint per replayed document | `Aggregate_Replay_PinsBounded` |
| Vector search (`VectorIndexQuery.Run`) | `Search(...).ToArray()` before the first yield; the traversal and its document loads are eager, so streaming the results alone changes nothing | safepoint at traversal boundaries inside `VectorIndexService.Search` (per visited node, after the node's neighbours are read and before the next hop), with the current node re-fetched through the snapshot afterwards; and one per materialized result in the document-loading loop that follows the traversal (`VectorIndexService.cs:121`), which traversal safepoints do not cover | `VectorSearch_PinsBounded`, `VectorSearch_Materialization_PinsBounded` |
| Vector writes: insert / update / delete on a collection with a vector index | `VectorIndexService.Upsert` calls `Delete`, whose `TryFindNode` breadth-first walks the whole graph even for a new document; measured 63 pages for one insert at a 16-page threshold | safepoint per visited node in `TryFindNode` and in the insert-time neighbour search, with reload of every retained node, the one being inserted included (`Insert` keeps it across `SearchLayer` and then calls `SetNeighbors`); or index the data-block → node mapping so `Delete` does not search | `VectorInsert_PinsBounded`, `VectorUpdate_PinsBounded`, `VectorDelete_PinsBounded` |
| Vector index construction and deletion | `EnsureVectorIndex` walks every document and inserts; `Drop` walks the graph through `ClearTree`; measured 56 pages for a drop | safepoint per visited node with reload | `VectorIndexBuild_PinsBounded`, `VectorIndexDrop_PinsBounded` |
| `Insert` / `Update` / `Upsert` loops (no vector index) | check per document at the top of the loop | none | existing |
| `Delete` by ids | check per *found* document; an absent id runs `continue` first (`Delete.cs:38`) | safepoint per attempted id, before the `continue` | `Delete_AbsentIds_PinsBounded` |
| `EnsureIndex` | check per document, after inserting all of its keys (`Index.cs:91`) | acceptable (one document's keys); document it | existing |
| `DropIndex` | none | safepoint callback per PK node, as `DropCollection` already has | `DropIndex_PinsBounded` |
| `DropCollection` | callback per page | none | existing |
| `Rebuild` | per document | none | existing |
| Collection page | never released by `Snapshot.Clear()` | none; 1 per snapshot | — |

**Safepoint safety.** Today's checked paths are safe by construction:
`IndexNode` copies `Next`/`Prev`/`Key`/`DataBlock` into managed fields in
its constructor and `IndexService.FindAll` reads only those after a `yield`;
`DataService.Read` yields slices that `BufferReader` consumes before the
consumer's `Safepoint()`. Every new safepoint in the table above sits inside
a method that holds page-backed objects, and those objects do not survive
it: a safepoint hands the frames to the readers and clears the snapshot
without freeing anything (5.2, lifetime checks), so a retained node keeps a
valid-looking slice into a frame the transaction no longer owns. The rule
for every row: carry `PageAddress`es across a safepoint and reload every
page-backed object touched afterwards, including objects held by the
*caller* of the method that safepoints. `VectorIndexService.Insert` is the
concrete case: it keeps the newly inserted node across `SearchLayer` and
then calls `node.SetNeighbors`; a safepoint inside the search means the
outer node must be re-fetched by address before that call. The debug
ownership check in 5.2 fails on the first violating access; the tests for
the vector rows additionally checkpoint, reopen and verify the index
(search results and neighbour lists), because a page-count assertion cannot
see a lost write.

**Wiring.** `IndexService`, `VectorIndexService` and `DataService` are
constructed from a `Snapshot`, and a `Snapshot` has no reference to its
transaction (`SnapShot.cs:20-24`: disk, WAL index, transaction id). The
safepoint is a delegate the transaction hands to the snapshot at creation
(`Snapshot.Safepoint`), the way `DropCollection` already receives one per
call; the services call it. A snapshot created outside a transaction (tests)
gets a no-op.

### 5.4 Segment sizing

- First segment: 8 pages (64 KB). Baseline per open engine drops from ~100 KB
  to ~70 KB plus streams (RC6). The LOH argument in the current comment does
  not apply to a long-lived array.
- All further segments: 128 pages (1 MB). Finer granularity means segments
  empty out and get released sooner, and the LOH free-list reuses 1 MB
  blocks well. 8,192 frames at 64 MB is a trivial object count.
- `MEMORY_SEGMENT_SIZES` stays as a constructor parameter for tests.

### 5.5 `:memory:` and stream-backed databases

Phase 4 (cheap, safe):
- Cache default 8 MB for `MemoryStream`-backed data (5.1).
- Log shrinking after checkpoint goes through the stream abstraction, not
  through `MemoryStream.Capacity` directly: `AesStream` keeps a one-page
  header, so logical length 0 is physical length `PAGE_SIZE`, and
  `ConcurrentStream` wraps both. `IStreamFactory` gains
  `TrimCapacity(Stream)`, implemented only for streams the engine owns
  (`:memory:`, `:temp:`), which sets the underlying capacity to the physical
  length the wrapper reports. `DiskService.SetLength(0, Log)` calls it.
- Streams that LiteDB creates itself are owned by the engine and disposed on
  `Close()`: the `:memory:`/`:temp:` data streams, and also the log and sort
  streams `EngineSettings.CreateLogFactory`/`CreateTempFactory` create when
  the caller supplied a `DataStream` but no `LogStream`/`TempStream` (a
  `MemoryStream` or `TempStream` today, never disposed). A caller-supplied
  stream is never disposed by the engine. Fixes the `TempStream` temp-file
  leak (#2056); `StreamFactory` gets an `ownsStream` flag.

Later (larger, optional): a page-source abstraction so a `MemoryStream`
"file" can hand out `PageBuffer`s over its own storage without copying into
the cache. Out of scope for this proposal; noted so the design does not
preclude it.

### 5.6 Hygiene

- `FileStreamFactory.GetStream()`: the wrong-password path is already covered,
  `AesStream`'s constructor disposes the underlying stream in its `catch`
  (`AesStream.cs:156`). The uncovered path is `File.SetAttributes` for hidden
  files throwing after the `FileStream` exists; wrap it. #2579's report
  predates or misattributes the current behaviour and should be re-verified
  against 5.0.21 before closing.
- `DiskService` constructor: dispose already-created pools and streams when a
  later step throws (#2614).
- `TransactionMonitor.Dispose()`: dispose the `ThreadLocal<TransactionService>` slot.
- `MemoryCache.Dispose()`: drop all segments, so a disposed engine still
  referenced by a container releases its memory immediately.

### 5.7 Observability

`SELECT $ FROM $database` → `cache`:

```
limitBytes, limitPagesRounded, allocatedBytes, segments, totalPages, freePages,
readablePages, idleReadablePages, writablePages, loadingPages, pinnedPages,
retainedBySegments, evictedPages, releasedSegments, overflowSegments,
framesExamined, budgetExceeded, lostFrames (debug), hits, misses,
compiledExpressions
```

`transactions` additionally reports `transactionPageLimit` and the current
`transactionPages` per open transaction (pages since the last safepoint, not a pin count). Two fields disappear with phase 3:
`availableSize` and `initialTransactionSize` describe the monitor's shared
page pool (`SysDatabase.cs:50-51`), which the fixed threshold removes; the
measurement program in appendix A now reports the fixed threshold and changes with
them. Nothing else in the public API changes.

### 5.8 Expression cache

The memory fix and a compatibility decision, kept apart:

1. **Bound compiled delegates** (the memory fix, phase 2): one fixed array of
   1,000 slots is shared by scalar and enumerable delegates. Source hashes select
   slots; immutable source/delegate pairs are published with `Interlocked.Exchange`
   and read with `Volatile.Read`. Hash collisions replace a single entry. Readers
   verify both the source and delegate type, so collisions only affect hit rate.
   Compilation remains outside synchronization, and existing expression instances
   retain their own delegates independently of eviction. A maintained atomic
   occupied-slot count replaces both the admission lock and dictionary-wide clear.
   This supersedes revision 5's `_cacheSync` implementation.
2. **Parameterized `Query.*` helpers are an opt-in API, not a change to the
   existing one.** The leaf change alone is not enough (revision 2:
   `Query.And`/`Or` build `($left.Source AND $right.Source)` through the
   implicit string conversion and drop both operands' `Parameters`), and
   changing the existing helpers is not compatible either:
   `BsonExpression.Source` and `implicit operator string` are public
   (`BsonExpression.cs:147`), so `string predicate = Query.EQ("value", 7)`
   is a self-contained expression today and would carry an unbound `@0`
   afterwards (reproduced in review: the literal text evaluated to true; the
   parameterized text converted to a string and reparsed evaluated to
   false). Callers who compose by string, log the text, or hand it to
   `Execute(string)` would break silently. Therefore:
   - The existing `Query.*` helpers keep producing literal text. With the
     cap in place they cost a parse and compile per distinct value, not
     memory.
   - A parallel entry point (working name `Query.Parameterized`, same
     method set: `EQ, GT, GTE, LT, LTE, Not, Between, StartsWith, EndsWith,
     Contains, In`, the `QueryAny` variants, `And`, `Or`) produces
     `BsonExpression.Create($"{field} = @0", value)`; the source text is
     constant per field and operator. The LINQ visitor already emits
     parameters and needs nothing. Migration guidance: switch when the value
     space is unbounded and the expression is never stringified. Making the
     implicit string conversion inline parameter values would rescue the
     string round-trip but not direct `Source` readers, and would give
     `Source` two meanings; rejected.
   - `And`/`Or` in the parameterized set merge operands with a
     collision-free renaming, not an offset. Parameters may be sparse (`@1`
     alone), named (`@value` on both sides with different values,
     `BsonExpressionParser.cs:817`), repeated within one operand, and
     already-composed. The composer walks each operand's source with
     `Tokenizer`, collects every referenced parameter name in order of first
     appearance, assigns fresh positional names `@0..@k` across both
     operands (left first), re-emits the tokens, and builds one parameter
     document by looking each original name up in its own operand's
     `Parameters`. String literals are untouched because they are single
     tokens. The composed source text is constant per shape, so the compile
     cache still hits.
   - **Value-snapshot semantics are preserved.** Today `Query.In(field,
     array)` serializes the array into the expression text, so later
     mutation of the caller's array does not change the query; a retained
     parameter would (reproduced: adding a value to the input array after
     construction made the parameterized form match it). The parameterized
     helpers therefore deep-clone mutable inputs (`BsonArray`,
     `BsonDocument`, binary) into the parameter document at construction.
     Tests mutate an array, a document and a byte array after building the
     query and assert the literal form's results.
   - Index selection keys on `expr.Right.IsValue` (no field references),
     which a parameter node satisfies, so plans are unchanged; the tests in
     section 6 assert this for every helper, composed and not.

## 6. Tests and verification

**CI first.** `LiteDB.Tests.csproj` contains
`<Compile Remove="Internals\**" />` for the Release configuration, so
`Cache_Tests`, `Disk_Tests` and everything else under `Internals/` do not run
in a Release test pass. Either remove the exclusion or move the cache tests
to `Engine/`, and make the CI workflow run them, before any of the tests
below count as verification.

Unit (`MemoryCache` with tiny segments; deterministic, no timing):
- `Load_ConcurrentReadersOfOneKey_LoseNoFrame`: a blocking factory holds the
  first loader; a second reader arrives; after both complete,
  `TotalPages == free + readable + writable` exactly and one frame is
  readable.
- `Load_FactoryThrows_ReturnsFrameAndUnblocksWaiters`: factory throws for the
  first caller; waiter retries and succeeds; no frame lost.
- `Pin_AfterEvictAndReuse_CannotPinStaleFrame`: reader A resolves a key, is
  paused (test hook) before pinning; the frame is evicted and reused for
  another key; A resumes and must end up with a frame for its own key. With
  the lock design this is enforced structurally; the test guards against a
  future lock-free rewrite.
- `WritableCopy_SourceCannotChangeDuringCopy`: eviction requested during a
  `GetWritablePage` copy is deferred until the copy completes.
- `Invalidate_AfterCheckpoint_ReadsNewContent`: write, checkpoint, read the
  same page id; content is the checkpointed content, and no log-position key
  survives in the index.
- `Trim_AllPinned_TerminatesWithoutProgress`: every frame pinned; `TrimToLimit`
  returns, `overflowSegments > 0`, no spin.
- `Trim_ReleasesSegments_AfterPeak`: pin four times the limit, release all,
  trim; `TotalPages <= LimitPagesRounded`, released segments > 0, every
  released `byte[]` collectable (weak references).
- `Cache_AllIdleAllReferenced_EvictsOnSecondPass`: every frame idle with
  `Referenced = 1`; acquisition does not allocate past the rounded limit and
  trim makes progress.
- `Acquire_BudgetExhausted_EvictsBeforeAllocating`: 8,200 idle frames, every
  reference bit set, scan budget 256; one miss clears bits, keeps sweeping
  and evicts; `segments` unchanged, `budgetExceeded == 1`.
- `Invalidate_WithinLimit_ReleasesNoSegment`: write 8 MB, checkpoint;
  `releasedSegments` stays 0 while the pool is within `LimitPagesRounded`;
  the same sequence with the pool over the limit releases only the excess.
- `Trim_ScatteredPins_ReportsRetention`: one pinned frame per segment across
  N segments; trim releases nothing, `retainedBySegments == (N × 128) − N`,
  and returns.
- `Pin_ConcurrentIncrementDecrement_NeverLosesADecrement`: readers pin and
  release concurrently; final count is zero.
- `Release_LastPin_MovesSegmentToReleasableBucket` and
  `Release_ConcurrentRepin_KeepsSegmentBusy`: the last release of a frame
  updates its segment's `Busy` count and bucket; a reader pinning the same
  frame concurrently leaves the segment busy and trim never releases it.
- `WriteLogDisk_PositionsRecordedBeforeRelease`: a controlled source iterator
  observes the `(pageID, position)` callback while the frame still has a
  pin; a competing thread cannot reuse the frame before the callback.
- `MoveToReadable_KeyCollision_FailsInvariant`: publishing a writable frame
  under a key that is already readable fails the `ENSURE`, leaves the
  existing frame untouched and the writable frame discardable; there is no
  overwrite path to test.
- `Safepoint_RetainedNode_WriteFailsOwnershipCheck` (`DEBUG || TESTING`):
  obtain an `IndexNode` in a write transaction, force a safepoint, call
  `SetNextNode`; the slice check fails on `State != Writable` although the
  frame's generation is unchanged and its position valid.
- `WriteLogDisk_WriteThrows_ReleasesFrame` and
  `WriteLogDisk_CallbackThrows_ReleasesFrame` (failure injection through
  `SimulateDiskWriteFail`).
- `Load_WaiterWakesAfterFrameReused_ReResolvesKey`: loader publishes, the
  waiter is held before it re-acquires the lock, the frame is evicted and
  reused for another key; the waiter must end with a frame for its own key.
- `Segment_ReleasedButHeldBySuspendedCursor_NotCollectable`: release a
  segment while a half-consumed enumerator holds an `IndexNode` into it;
  the weak reference stays alive until the enumerator is disposed, and
  `releasedSegments` already counts it.
- `Acquire_FramesExaminedBounded`: with 90% of frames pinned, repeated misses
  examine at most `EvictScanBudget` frames each (counter assertion).
- `Trim_PrefersReleasableSegments`: scattered pins in half the segments;
  trim releases the unpinned segments and leaves the pinned segments' idle
  readable pages in place.
- `Cache_NeverExceedsRoundedLimit_WhenPagesAreIdle`,
  `Cache_AllocatesFromMostPopulatedSegment`,
  `Cache_ReferencedBit_ProtectsHotPages` as in revision 1, with bounds
  expressed against `LimitPagesRounded`.
- Debug-mode poison: a frame that becomes Free under `DEBUG || TESTING` is
  filled with `0xFF` and its `Generation` bumped; `BasePage` asserts the
  generation it captured on its own accesses, and slices assert generation,
  snapshot epoch and, for writes, writable ownership (5.2).

Engine:
- `Scan_DoesNotGrowCacheBeyondLimit` (#2278, #2619): 300 MB file,
  `cache size=32MB`, two streamed scans; `allocatedBytes <= rounded limit`,
  `pinnedPages == 0` afterwards.
- `OpenCursor_PinsAtMostThresholdPlusOneDocument`: half-consumed enumerator;
  `pinnedPages <= TransactionPageLimit + pages of one document`.
- Safepoint regression tests, one per row of the 5.3 audit
  (`OrderBy_NoIndex_PinsBounded`, `Include_Array_PinsBounded`,
  `Like_NoMatch_PinsBounded`, `Aggregate_Replay_PinsBounded`,
  `VectorSearch_PinsBounded`, `VectorInsert/Update/Delete_PinsBounded`,
  `VectorSearch_Materialization_PinsBounded`,
  `VectorIndexBuild/Drop_PinsBounded`, `DropIndex_PinsBounded`,
  `Delete_AbsentIds_PinsBounded`, `IndexIn_AbsentValues_PinsBounded`,
  `NotEqual_NoMatch_PinsBounded`): run with `TransactionPageLimit = 16`,
  assert the maximum `TransactionSize` observed through a test hook is
  ≤ 16 + one document's pages. Each test is added with the corresponding
  safepoint change and fails against today's code with the numbers in the
  5.3 table.
- Persistence after safepoints: every vector-row test and
  `DropIndex_PinsBounded` additionally checkpoint, reopen the file and
  verify the index (search results, neighbour lists, or that the dropped
  index is gone), because a write lost after a safepoint leaves every page
  count unchanged.
- `BulkWrite_ReusesFrames` (#1756), `MemoryDb_LogCapacityReleasedOnCheckpoint`
  (#2541, encrypted and unencrypted), `TempStream_DeletedOnDispose` (#2056),
  `HiddenFile_SetAttributesFailure_DisposesStream`,
  `DiskServiceCtor_Failure_DisposesPools` (#2614).
- `QueryHelpers_KeepPlans`: for every `Query.*` helper and for `And`/`Or`
  compositions, the execution plan (`ExplainPlan`) and the result set equal
  the literal version's, and `Parameters.Count` equals the number of values.
- `QueryCompose_RenamesParameters`: sparse numeric names (`@1` only on the
  left), named collisions (`@value` on both sides with different values),
  repeated references inside one operand, nested `And(Or(a, b), c)`, and
  `"@0"` inside a string literal; each evaluates identically to its literal
  form.
- `QueryHelpers_SnapshotMutableInputs`: mutate the array, document and byte
  array after constructing `In`/`EQ`; results equal the literal form's.
- `ExpressionCache_CapHoldsUnderConcurrency`: 16 threads compiling distinct
  expressions; the dictionary never exceeds the cap.
- `QueryEq_DoesNotGrowExpressionCache` (#1688, #2421).
- Existing `Transactions_Tests` safepoint tests keep passing.

Benchmarks (`LiteDB.Benchmarks`): point lookups, full scan, bulk insert,
bulk update on an encrypted file, and vector search, each at `cache size`
8/64/256 MB against the current engine, on net8.0 and net10.0; plus two
latency benchmarks that the single-lock design makes necessary: concurrent
readers (1/4/16 threads of point lookups, p50/p99 per lookup, frames
examined per acquisition) and transaction-end latency with `TrimToLimit`
active (p99 of `ReleaseTransaction`); and a third axis, `EnsureIndex` on a
large collection (200,000 and 900,000 documents) with wall time and final
log file size recorded, fixed threshold against today's extensible budget.
The threshold rewrites every dirty hot page once per safepoint and an index
build dirties nearly every page it touches; a safepoint flush is
`stream.Flush()` (`DiskService.cs:205`), a buffer flush, not an fsync, so
the cost is bytes and log growth, not syncs, and it has to be known before
the 1,000-page default is confirmed. Defaults in 5.1 and the decision to
keep one lock are confirmed or changed from these numbers, not before.

## 7. Rollout

Ordered so that eviction is correct before it becomes frequent. Revision 1
shipped a "recycle whenever at the limit" patch first, which would have raised
eviction frequency on top of the unfixed loading and eviction races.

| Phase | Change | Risk | Effect on the issue list |
|---|---|---|---|
| 1 | Frame ownership and states; `TryAdd`-based publication with loser and failure cleanup; pin and release bookkeeping under the lock; `MoveToReadable` key collision as an invariant failure; writable copy under the lock; `WriteLogDisk`/`ReturnNewPages` record `(pageID, position)` before release; checkpoint invalidation preserved; maintained counters; CI runs `Internals/**` | Medium; touches the hot path, but behaviour-preserving | Removes the lost-frame bug, the WAL lifetime gap and the RC4 exception path: #2252, #2282, #2574 |
| 2 | Bound the compiled-expression caches with a maintained count (alone). Parameterized helpers are a separate opt-in API decision (5.8), not part of the memory fix | Low; independent of the engine | #1688, #2421, #2395 |
| 2b | Stream ownership and disposal: owned `:memory:`/`:temp:` streams, `TrimCapacity`, `DiskService` constructor cleanup, hidden-file attribute failure | Low; independently reviewable | #2056, #2614, #2541 (log capacity) |
| 3 | `CacheSize` and `TransactionPageLimit` settings and connection string; remove `TryExtend`/`GetInitialSize`; the safepoint audit of 5.3 with its regression tests and the `Snapshot.Safepoint` delegate; `$database` fields (`availableSize` and `initialTransactionSize` go away; the reflection helper in `Transactions_Tests.cs:437` that sets `_freePages` is updated) | Low–medium; changes safepoint frequency for transactions that used to extend | Reduced transaction retention. No growth bound is claimed yet: without phase 4 the cache still cannot enforce `CacheSize` |
| 4 | Segment tracking, CLOCK eviction with the measured scan budget, `TrimToLimit` at transaction end, segment sizing (5.4), retention accounting; `Cache_Read_Write` and `Cache_Extends` are rewritten, they encode today's segment ramp and hysteresis | Medium; core structure rewrite, covered by section 6 | Enforcement of the soft target and memory return after peaks: #2278, #2619, #2311, #1756, #1479 |
| 5 | Defaults chosen from the benchmarks in section 6 (including encrypted writes and vector workloads) | Low | — |

Phases 1 and 4 rewrite the same hot path. They are developed on one branch,
with phase 1 as its own reviewable commit that passes the full suite, so the
correctness fixes can be reviewed and bisected apart from the structural
rewrite.

The default `CacheSize` remains the one behaviour change users can notice:
databases larger than the limit no longer end up fully cached.
`cache size=1GB` restores the old behaviour.

## 8. Alternatives considered

- **Time-based eviction / background trimmer thread.** Rejected: LiteDB has
  no background threads today (the writer queue was removed for that reason),
  timers make behaviour non-deterministic and hard to test, and every trim
  point we need (transaction end, checkpoint) is already a synchronous event.
- **`ArrayPool<byte>.Shared` for segments.** Rejected: the shared pool keeps
  released arrays per core and only trims under GC pressure, so accounting
  becomes opaque; plain `new byte[]` + drop gives the GC the whole story.
- **Weak references to idle segments.** Rejected: the GC would free hot data
  under pressure with no relation to the access pattern, and `PageBuffer`
  slices keep the arrays alive anyway.
- **Only lowering `MAX_TRANSACTION_SIZE`.** Necessary but not sufficient:
  it caps the *growth* (phase 3) but nothing shrinks without segment
  tracking (phase 4).
- **Per-transaction hard limit that throws.** Rejected: a transaction must
  be able to touch more pages than the cache holds (it releases at
  safepoints); throwing turns a memory concern into a correctness failure.

## 9. Issue catalog

Source: `litedb-org/LiteDB` tracker, all states, searched for memory, leak,
OutOfMemory, cache, PageBuffer, RAM (2026-09-11). Grouped by the root cause
above; v2–v4 issues are listed only where the mechanism still informs v5.

### 9.1 Page cache grows and never shrinks (RC1–RC3) — 13 open

| # | State | Version | Reported scenario and numbers |
|---|---|---|---|
| 1756 | open | 5.0.8+ | Mixed insert/update/random read on 4 collections; `_free` 6,406 entries = 55 MB; later commenters: `_readable` 35k entries > 100 MB; 20 GB exhausted in a week; 300k docs loaded briefly. |
| 2020 | open | 5.0.10 | Three concurrent timers (insert, `Find().Count()`, `Find()`+`Update`) on one `Direct` db: growth in 8 MB steps; sequential variant does not grow. |
| 1848 | open | 5.0.9 | One writer + 3–4 reader threads, 200 MB db: OOM; single reader "pretty low". |
| 2074 | open | 5 | Asks for a cache limit; v4 `cache size` "seems to have no effect"; commenter asks to disable the cache for read-once workloads. |
| 1896 | open | 5 | Asks for a `cache_size` pragma equivalent. No reply. |
| 2311 | open | 5 | Long-lived app: ~150 MB cache never regained; reporter's analysis names `Extend`, LOH fragmentation, `_readable` all `ShareCounter == 0`. |
| 2619 | open, v6 label | 5.0.21 | Email client, never-closed db, read-only queries: > 1 GB; maintainer: "We could look into cache eviction". ASP.NET singleton reporter hits file locks when switching to per-operation instances. |
| 2278 | closed | 5.0.15 | 1 GB db, 10k-row blocks via `ToList()`: 1.5 GB RSS, 6 GB with `Find`; `ToEnumerable()` removed the growth. |
| 2289 | open | 5 | 50k docs `FindAll().ToList()`: 100+ MB retained with a static `LiteDatabase`. |
| 2400 | open | 5 | "Retrieving one million items", `Dispose()` does not free. No detail. |
| 2139 | open | 5.0.11 | OOM inside `MemoryCache.Extend()` after 1–2 weeks; per-call shared-mode instances; working set 668 MB, peak 1,196 MB. |
| 2092 | open | 5 | `Offset(i*10000)` paging over 7M docs: page time 1 s → 16 s and memory grows in parallel. |
| 1479 | open | 5 early | 100 open dbs = ~800 MB at the time; first segment later reduced to 12 pages (commit `ad231ffa`), now ~117 KB per engine (section 2.7). |
| 2174 | closed | 5 | 120 KB allocated per `new LiteDatabase`; maintainer: by design, initial cache allocation. |
| 2647 | open RFC | v6 | Proposal by the author of #1756's external fork; see section 10. |

Related invariant violations in the same structure (not growth, but the same
race the pin protocol in 5.2 removes): #2252, #2282 (`pages in memory store
must be non-shared`), #2574 (`discarded page must be writable` thrown from
`Dispose()`).

### 9.2 Static expression cache (RC8) — 2 open

| # | State | Version | Reported scenario and numbers |
|---|---|---|---|
| 1688 | closed by reporter | 5.0.8 | ETL job with `Query.EQ("RecordId", id)` per row; dotMemory blames `BsonExpressionScalarDelegate`; 2.5 GB after 5 days; reflection helper clears the dictionaries every 10 min. |
| 2421 | open | 5.0.17 | `Query.LT` etc.; > 4,000 dictionary entries; maintainer: "I will be looking into it". |
| 2395 | open | 5.0.16 | Infinite loop where only `DeleteMany(Query.EQ("Name", uniqueName))` grows; lambda variants do not. Inference: same mechanism. |

### 9.3 `MemoryStream`-backed storage (RC5) — 3 open

| # | State | Version | Reported scenario and numbers |
|---|---|---|---|
| 2541 | open | 5.0.x | `:memory:` bulk insert in 1k batches: OOM at `MemoryStream.set_Capacity` via `WriteLogDisk` and via `CheckpointInternal`. |
| 2524 | open | 5.0.16 | v3 upgrade reader: "Stream was too long" at `FileReaderV7.ReadExtendData`; likely a corrupted chain rather than size. |
| 1312 | open | 5 | Studio upgrade of a 2 GB v4 file uses ~1 GB. |
| 531 | closed | 3.1 | Shrink via `MemoryStream`: 300 MB db → 1 GB + OOM; fixed by batching; LOH fragmentation of `MemoryStream` noted then. |

### 9.4 Undisposed streams and transactions (RC7) — 4 open

| # | State | Version | Reported scenario |
|---|---|---|---|
| 2056 | open | 5.0.11 | `:temp:` db spilled to disk; temp file never deleted because `TempStream` is not disposed. |
| 2579 | open | 5.0.21 | Wrong password: `FileStream` orphaned when `AesStream` throws; file stays locked. Reporter supplied the fix. |
| 2614 | open | 5.0.21 | Disk full during `DiskService` construction: already-created streams/pools not disposed. |
| 2615 | closed, disputed | 5.0.21 | `TransactionService.Dispose` throws; `DiskReader` never returned; log file stays locked. |
| 2440 | closed, fixed (#2436) | 5.0.18 | `FindOne()` on a partially iterated cursor left the transaction and read lock open. |

### 9.5 Whole operation held in one transaction (v2–v4 design; RC2 is the v5 descendant)

#484, #533, #531 (fixed in v3: `InsertBulk`, batching), #1058, #137, #1244
(v4 `EnsureIndex` 700 MB), #1214 (v4 count over 10 billion records, 4.7 GB),
#1301 (v4 corrupted page chain), #2266 (v5 question quoting the 1 GB
transaction budget from `Constants.cs`).

### 9.6 Caller-side materialization

#865 (`Engine.Run` returned a `List`), #670 (v3 LINQ translated to two
queries plus `Except`; fixed in v3.5), #2278/#2289/#2139 (also in 9.1).

### 9.7 Fixed long ago, still open

#1345: `PageBuffer` finalizers kept 29,001 buffers alive one extra
generation; fixed by PR #1351 (2019). Closable.

## 10. Relation to upstream pull requests

Upstream `dev` (the default branch) and `master` still ship the original
`MemoryCache.cs`, byte-identical to this fork. Five PRs have touched the
area; none is merged.

| PR | State | What it changes | Why it is not enough |
|---|---|---|---|
| #2644 "Fix cache reuse to limit memory growth" (JKamsker, Codex-generated, base `dev-staging`) | open, unreviewed | `Extend()` recycles whenever *any* idle readable page exists (`emptyShareCounter > 0`), allocates only if nothing could be recycled. Fixes RC3. | Recycling still runs a full `Count` + LINQ sort of `_readable` under the lock, now on every miss with few idle pages (thrash; a write burst flushes the warm read set oldest-first). Widens the `GetOrAdd`/`Increment` race (RC4) because eviction becomes frequent. No limit, no release, no pin bound. |
| #2649 "Stage 1 improvements (RFC #2647)" (zalza13, base `dev`) | open, changes requested, author stalled since 2025-10 | Adds `_evicting` flag on `PageBuffer`, a `SemaphoreSlim`-guarded cleanup every 256 reads that dequeues up to 128 free pages idle for 60 s, zeroes them and drops them; `CacheProfile` enum hard-wired to Desktop. | Dropping a `PageBuffer` frees ~50 bytes; the 8 MB segment stays rooted by its sibling slices, while `ExtendPages` keeps counting the dropped slot, so the pool shrinks and the next miss allocates a fresh segment. Cleanup runs on reader threads, drains `_free` mid-cleanup (concurrent `GetFreePage` extends), `_free.Count` is O(n), the signal can be lost, the eviction flag is checked by nobody else. Maintainer asked for cheaper counters and a dynamic profile type. |
| #2624 "Add configurable cache limit and regression test" (Codex-generated, base `master`, draft) | closed 2025-09-27 without comment | `EngineSettings.CacheSize` + `cache size=` connection-string key (default 256 MB, 0 = unlimited), `_segments` list + `_totalPages`, reclaim-first `TryExtend/Reclaim/AllocateSegment`, and a hard cap that spins four times then **throws `LiteException 138 CacheLimitExceeded`**. Bundled with 30 unrelated files including a `DateTime` truncation change flagged P1. | The cap is below the transaction budget and writable/pinned pages are never reclaimable, so large transactions and concurrent readers turn "uses RAM" into an exception. Same per-miss sort thrash as #2644. Never releases memory. |
| #1609 "switched to concurrent cache" (2020) | open, abandoned | v4 `CacheService` thread-safety. | Different engine. |
| #2438 "Fixed memory leak due to infinite static caches" (2024) | closed by JKamsker: "the problem this pr addresses is real but its really not solved with this pr" | Replaces the two static `BsonExpression` dictionaries with a `SlidingCache` + timer; default still unbounded, opt-in via a static `CacheSlidingExpiration`. | Opt-in only; timer-based; reviewer concerns about an untestable clock. Maintainer doubted whether dropping a compiled delegate frees anything. |

How this proposal positions against them:

- It adopts the two things those PRs got right: `CacheSize` in `EngineSettings`
  and the connection string (from #2624, same key and size syntax), and
  reclaim-before-allocate (from #2644 and #2624).
- It replaces the per-miss sort with a CLOCK sweep over fixed frames (5.2),
  which is what removes the thrash and the read-set flush that make #2644 and
  #2624 risky. The reference bit gives hot pages a second chance, so a write
  burst no longer evicts the warm read set oldest-first.
- It makes the limit soft and pairs it with a fixed per-transaction safepoint
  threshold (5.3) instead of throwing (#2624). A limit that can be exceeded only by pinned pages is
  enforceable; a hard limit that ignores pins is not.
- It tracks segments as first-class objects with per-segment free lists so
  that releasing a segment is a real operation (5.2), which is the piece
  #2649 attempted without segment liveness and therefore could not deliver.
- It takes #2649's eviction-flag idea but makes eviction a state transition
  under the same lock that publishes and pins frames (5.2), so it actually
  protects something; #2649's flag was only ever read by the cleanup that set
  it.
- For the expression cache it removes the cause rather than adding a timer
  (5.8): parameterized `Query.*` helpers make the cache key constant, and a
  size cap with clear-on-overflow bounds the remaining growth. Regarding the
  maintainer's doubt on #2438: delegates produced by `Expression.Compile()`
  are backed by collectible dynamic methods, so dropping the last reference
  does free them; with parameterization the question mostly disappears
  because far fewer delegates are created.
- Two suggestions from the #2649 review are taken as-is: maintained counters
  instead of `ConcurrentQueue.Count`, and a configurable object rather than
  an enum for tuning. The timing-wheel idea mentioned there is
  not needed once eviction is demand-driven and trim points are transaction
  end and checkpoint.

## Appendix A — measurement program

Console project referencing `LiteDB/LiteDB.csproj` (net8.0, Release,
workstation GC, `InvariantGlobalization`). Run as
`MemRepro <scan|holdcursor|point|write|memdb|manydb|dispose> [docs]`.

```csharp
using System.Diagnostics;
using LiteDB;

static class P
{
    static string Dir = Path.Combine(AppContext.BaseDirectory, "data");

    static void Main(string[] args)
    {
        Directory.CreateDirectory(Dir);
        var scenario = args.Length > 0 ? args[0] : "scan";
        var n = args.Length > 1 ? int.Parse(args[1]) : 200_000;
        switch (scenario)
        {
            case "scan": Scan(n); break;
            case "point": Point(n); break;
            case "write": Write(n); break;
            case "manydb": ManyDb(n); break;
            case "memdb": MemDb(n); break;
            case "holdcursor": HoldCursor(n); break;
            case "dispose": DisposeCheck(n); break;
        }
    }

    static BsonDocument Doc(int i) => new BsonDocument
    {
        ["_id"] = i,
        ["name"] = "user-" + i,
        ["email"] = $"user{i}@example.com",
        ["age"] = i % 90,
        ["payload"] = new string('x', 900),
    };

    static string Report(LiteDatabase db, string label)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var heap = GC.GetTotalMemory(true) / 1024 / 1024;
        var ws = proc.WorkingSet64 / 1024 / 1024;
        var line = $"{label,-38} heap={heap,5} MB  ws={ws,5} MB";
        if (db != null)
        {
            var info = db.Execute("SELECT $ FROM $database").First().AsDocument;
            var c = info["cache"].AsDocument;
            var t = info["transactions"].AsDocument;
            line += $"  cache: segs={c["segments"].AsInt32,3} pages={c["totalPages"].AsInt32,6} ({c["allocatedBytes"].AsInt64 / 1024 / 1024,4} MiB) free={c["freePages"].AsInt32,6} readable={c["readablePages"].AsInt32,6} writable={c["writablePages"].AsInt32,5} inUse={c["pinnedPages"].AsInt32,5}  tx: open={t["open"].AsInt32} limit={t["transactionPageLimit"].AsInt32}  log={info["logFileSize"].AsInt32 / 1024 / 1024} MB";
        }
        Console.WriteLine(line);
        return line;
    }

    static string Seed(int n, string name = null)
    {
        var file = Path.Combine(Dir, name);
        if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
        File.Delete(file); File.Delete(Path.ChangeExtension(file, "-log.db"));
        using var db = new LiteDatabase(file);
        var col = db.GetCollection("users");
        col.EnsureIndex("age");
        for (var i = 0; i < n; i += 10_000)
            col.Insert(Enumerable.Range(i, Math.Min(10_000, n - i)).Select(Doc));
        db.Checkpoint();
        return file;
    }

    static void Scan(int n)
    {
        var file = Seed(n, n == 200_000 ? "scan.db" : $"scan-{n}.db");
        Console.WriteLine($"data file: {new FileInfo(file).Length / 1024 / 1024} MB, docs={n}");
        var db = new LiteDatabase(file);
        Report(db, "opened");
        var col = db.GetCollection("users");
        var cnt = col.Count();
        Report(db, $"Count() = {cnt}");
        var c1 = col.FindAll().Count();
        Report(db, "FindAll() streamed once");
        var c2 = col.FindAll().Count();
        Report(db, "FindAll() streamed twice");
        var list = col.FindAll().ToList();
        Report(db, "FindAll().ToList() (held)");
        list = null;
        Report(db, "list dropped");
        var young = col.Find(Query.LT("age", 10)).Count();
        Report(db, $"index range age<10 = {young}");
        db.Dispose(); db = null;
        Report(null, "db disposed");
    }

    static void Point(int n)
    {
        var file = Seed(n, "scan.db");
        using var db = new LiteDatabase(file);
        var col = db.GetCollection("users");
        Report(db, "opened");
        var rnd = new Random(1);
        for (var round = 1; round <= 5; round++)
        {
            for (var i = 0; i < 20_000; i++) col.FindById(rnd.Next(n));
            Report(db, $"{round * 20_000} FindById");
        }
    }

    static void Write(int n)
    {
        var file = Path.Combine(Dir, "write.db");
        File.Delete(file); File.Delete(Path.ChangeExtension(file, "-log.db"));
        using var db = new LiteDatabase(file);
        var col = db.GetCollection("users");
        col.EnsureIndex("age");
        Report(db, "opened");
        var batch = 1000;
        for (var i = 0; i < n; i += batch)
        {
            col.Insert(Enumerable.Range(i, batch).Select(Doc));
            if (i > 0 && i % 5000 == 0)
            {
                col.UpdateMany("{payload: 'updated'}", "age = 5");
                col.DeleteMany("age = 7 AND _id < " + i);
            }
            if ((i / batch) % 50 == 49) Report(db, $"inserted {i + batch}");
        }
        Report(db, "done");
        db.Checkpoint();
        Report(db, "after Checkpoint()");
    }

    static void ManyDb(int n)
    {
        Report(null, "start");
        var dbs = new List<LiteDatabase>();
        for (var i = 0; i < n; i++)
        {
            var file = Path.Combine(Dir, $"many-{i}.db");
            File.Delete(file);
            var db = new LiteDatabase(file);
            db.GetCollection("c").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "x" });
            dbs.Add(db);
        }
        Report(dbs[0], $"{n} dbs open");
        foreach (var d in dbs) d.Dispose();
        dbs.Clear();
        Report(null, "all disposed");
    }

    static void MemDb(int n)
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection("users");
        Report(db, "opened :memory:");
        for (var i = 0; i < n; i += 10_000)
        {
            col.Insert(Enumerable.Range(i, Math.Min(10_000, n - i)).Select(Doc));
        }
        Report(db, $"inserted {n} (~{n * 1000 / 1024 / 1024} MB raw)");
        var c = col.FindAll().Count();
        Report(db, "FindAll streamed");
        db.Checkpoint();
        Report(db, "after Checkpoint()");
    }

    static void DisposeCheck(int n)
    {
        var file = Seed(n, "scan.db");
        var (wr, wre) = Open(file);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"after dispose: db alive={wr.IsAlive} engine alive={wre.IsAlive} heap={GC.GetTotalMemory(true) / 1024 / 1024} MB gen2={GC.CollectionCount(2)}");
        // now let the thread continue: ThreadLocal slot?
        var t = new Thread(() => { var (a, b) = Open(file); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Console.WriteLine($"[worker thread] after dispose+GC on same thread: db alive={a.IsAlive} engine alive={b.IsAlive}"); });
        t.Start(); t.Join();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"after worker thread exit: heap={GC.GetTotalMemory(true) / 1024 / 1024} MB");
    }

    static (WeakReference, WeakReference) Open(string file)
    {
        var db = new LiteDatabase(file);
        var engine = typeof(LiteDatabase).GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(db);
        var col = db.GetCollection("users");
        var c = col.FindAll().Count();
        Report(db, $"scanned {c}");
        db.Dispose();
        return (new WeakReference(db), new WeakReference(engine));
    }

    static void HoldCursor(int n)
    {
        var file = Seed(n, "scan.db");
        using var db = new LiteDatabase(file);
        var col = db.GetCollection("users");
        Report(db, "opened");
        // Half-consumed cursor left open (common: First()/Take() over a huge scan without disposing)
        var e = col.FindAll().GetEnumerator();
        for (var i = 0; i < n / 2; i++) e.MoveNext();
        Report(db, "cursor half consumed (open)");
        e.Dispose();
        Report(db, "cursor disposed");
    }
}
```
