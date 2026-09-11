# Bounded, elastic page cache for LiteDB v5 — proposal

Status: proposal (2026-09-11). Scope: `LiteDB/Engine/Disk/MemoryCache.cs`,
`DiskService`, `TransactionMonitor`, `TransactionService`, `EngineSettings`,
`ConnectionString`, `$database` system collection.

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

The fix proposed here makes the cache **bounded** (a configurable limit,
default 64 MB for file databases), **elastic** (segments are returned to the GC
when idle after a peak), and **honest about pins** (the transaction budget is
derived from the cache limit so pinned pages can no longer force the cache
past its limit by an order of magnitude). It keeps the on-disk format, the
public API, and the page-pinning protocol, and it removes one existing
"no way to fix" exception path in the cache.

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

### RC7 — stream disposal on failure

`FileStreamFactory.GetStream()` opens a `FileStream` and then constructs an
`AesStream`; if the latter throws (wrong password, salt read failure) the
`FileStream` is leaked (#2579).

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
| Bounded | A configurable byte limit on cache memory. Default 64 MB (8,192 pages). Idle pages never push the cache past it. |
| Elastic | After a peak the cache returns segments to the GC down to the limit, without timers. |
| Pin-honest | Pinned pages can exceed the limit only by a documented, small amount (`open transactions x per-transaction budget`), never by 800 MB. |
| Cheap | O(1) amortized page acquisition and eviction; no LINQ over the whole cache; no sort. |
| Race-free | Eviction and pinning use a compare-and-swap protocol; the "no way to fix" throw disappears. |
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
- Internal: `CachePages = CacheSize / PAGE_SIZE`.

### 5.2 `MemoryCache` v2: frames, segments, CLOCK

Replace the single `ConcurrentQueue<PageBuffer> _free` and the
timestamp-sort recycle with a real buffer pool:

```
Segment
  byte[]        Buffer          // 1 MB (128 pages) after the first segment
  PageBuffer[]  Pages           // fixed frames, never re-created
  int           FreeCount
  int           FreeHead        // intrusive free list through PageBuffer.NextFree
  int           Index           // position in _segments

PageBuffer (additions)
  Segment       Segment
  int           NextFree        // -1 when not free
  int           Referenced      // CLOCK reference bit (0/1)
```

State of a frame is encoded in the existing `ShareCounter`:

| `ShareCounter` | meaning |
|---|---|
| `>= 1` | readable, pinned by n readers |
| `0` | readable and idle (evictable) **or** free (distinguished by `Position == long.MaxValue`) |
| `-1` (`BUFFER_WRITABLE`) | private writable copy owned by one transaction |
| `-2` (`BUFFER_EVICTING`, new) | being evicted; readers must retry |

**Acquire a free page** (`GetFreePage`, under one uncontended `_sync` lock):

1. Pop from the current allocation segment's free list. Prefer the most
   populated segment with free frames so that nearly-empty segments drain
   and become releasable (first-fit over `_segments` ordered by `FreeCount`
   ascending; the list is short).
2. If no free frame anywhere and `TotalPages < CachePages`: allocate a
   segment (1 MB).
3. Otherwise run the CLOCK sweep to evict up to `EvictBatch` (= 128) idle
   readable frames, then retry step 1.
4. If the sweep evicted nothing (everything pinned or writable): allocate a
   segment anyway and increment `OverflowSegments`. This is the soft-limit
   escape hatch; section 5.3 makes it rare and bounded.

**CLOCK sweep**: a hand `(segmentIndex, frameIndex)` walks the frames. For a
frame with `ShareCounter == 0 && Position != MaxValue`:
`if (Referenced == 1) Referenced = 0; else evict`. Frames that are pinned,
writable or free are skipped. At most two full turns per call. No sort, no
allocation, no dictionary enumeration.

**Evict one frame** (race-free):

```csharp
if (Interlocked.CompareExchange(ref page.ShareCounter, BUFFER_EVICTING, 0) != 0) continue;
_readable.TryRemove(new KeyValuePair<long, PageBuffer>(key, page)); // remove only if still mapped to this instance
page.Position = long.MaxValue; page.Origin = FileOrigin.None;
page.ShareCounter = 0;                // now "free"
segment.PushFree(page);
Interlocked.Increment(ref _evicted);
```

**Pin on read** (`GetReadablePage`), replacing the unconditional increment:

```csharp
while (true)
{
    var page = _readable.GetOrAdd(key, factory);
    var sc = Volatile.Read(ref page.ShareCounter);
    if (sc >= 0 && Interlocked.CompareExchange(ref page.ShareCounter, sc + 1, sc) == sc)
    {
        page.Referenced = 1;
        return page;
    }
    // sc == BUFFER_EVICTING (or a stale instance): the dictionary entry is
    // about to disappear; spin and re-resolve. Bounded by the evictor's few
    // instructions between CAS and TryRemove.
}
```

This closes RC4: an evictor can only win the CAS on a frame nobody pins, and a
reader that lost sees `-2` and re-resolves instead of pinning a frame that is
being freed. The `LiteException("removed in-use memory page ...")` path is
deleted.

**Release a segment**: when `PushFree` makes `FreeCount == Pages.Length`,
and the number of fully free segments exceeds one spare, and this is not the
first (small) segment: unlink the segment from `_segments`, drop the
`byte[]`. Because frames are only ever free (no reference from `_readable`,
no pin, no transaction) this is safe by the same invariant the current code
relies on for `_free`. `UniqueID` numbering stays monotonic; `ExtendPages`
becomes `TotalPages` (live count, not a sum over history).

**Trim**: `TrimToLimit()` = while `TotalPages > CachePages` evict idle frames
via CLOCK, then release empty segments. Called from
`TransactionMonitor.ReleaseTransaction()` (deterministic: every transaction
end) and from `WalIndexService.Clear()` (checkpoint), replacing
`MemoryCache.Clear()`. No background thread, no timer, no `GC.Collect()`.

**Writable pages** keep today's behaviour (`GetWritablePage` takes a fresh
frame and copies the readable content). They are counted in `TotalPages`
and are never evictable; `DiscardPage` pushes them back to their segment.

**Counters** replace the LINQ properties: `PagesInUse`, `WritablePages`,
`FreePages` become `Interlocked` counters maintained at state transitions.

### 5.3 Transaction budget derived from the cache limit

`TransactionMonitor` currently hands out a 100,000-page global budget in
1,000-page steps. Change to:

```
CachePages        = settings.CacheSize / PAGE_SIZE
PerTxInitial      = clamp(CachePages / 16, 1000, 8192)   // 64 MB -> 1000 pages (8 MB)
GlobalBudget      = CachePages * 3 / 4                   // 64 MB -> 6144 pages
extend step       = PerTxInitial while GlobalBudget has room; else Safepoint
```

- A scan transaction now releases its local pages every 1,000 pages instead
  of every 100,000. Release is a counter decrement per page; the pages stay
  readable in the cache, so re-reads within the working set are still hits.
- A bulk-write transaction persists dirty pages to the log every 1,000
  dirty pages instead of every 100,000. That is one extra `Flush()` per 8 MB
  of dirty pages, which is noise next to the page writes themselves (the
  constants file already notes 1,000 as the value used in tests).
- Worst case pinned above the limit:
  `open transactions x PerTxInitial` (with `MAX_OPEN_TRANSACTIONS = 100`
  and 64 MB: up to 800 MB only if 100 transactions are simultaneously at
  their limit; typical apps run 1–4). This bound is documented and exposed as
  `$database.cache.overflowSegments`.
- The `GetInitialSize()` "reduce every open transaction" fallback is removed.
  When `GlobalBudget` is exhausted a new transaction gets `PerTxInitial`
  anyway and the overflow counter records it; it will safepoint at 1,000
  pages.

Safepoint safety was verified for the iterators involved: `IndexNode` copies
`Next`/`Prev`/`Key`/`DataBlock` into managed fields in its constructor and
`IndexService.FindAll` only reads those after a `yield`; `DataService.Read`
yields buffer slices that `BufferReader` consumes synchronously before the
consumer's `Safepoint()`. Section 6 adds a debug-mode poison to keep it that
way.

### 5.4 Segment sizing

- First segment: 8 pages (64 KB). Baseline per open engine drops from ~100 KB
  to ~70 KB plus streams (RC6). The LOH argument in the current comment does
  not apply to a long-lived array.
- All further segments: 128 pages (1 MB). Finer granularity means segments
  empty out and get released sooner, and the LOH free-list reuses 1 MB
  blocks well. 8,192 frames at 64 MB is a trivial object count.
- `MEMORY_SEGMENT_SIZES` stays as a constructor parameter for tests.

### 5.5 `:memory:` and stream-backed databases

Phase 2 (cheap, safe):
- Cache default 8 MB for `MemoryStream`-backed data (5.1).
- `DiskService.SetLength(0, Log)`: if the underlying stream is a
  `MemoryStream`, also set `Capacity = 0` so the log buffer is released after
  checkpoint.
- Streams that LiteDB creates itself for `:memory:`/`:temp:` are owned by the
  engine and disposed on `Close()` (fixes the `TempStream` temp-file leak,
  #2056) — `StreamFactory` gets an `ownsStream` flag.

Phase 3 (larger, optional): a page-source abstraction so a `MemoryStream`
"file" can hand out `PageBuffer`s over its own storage without copying into
the cache. Out of scope for this proposal; noted so the design does not
preclude it (the cache already keys pages by `(Origin, Position)`, so an
in-place source only needs to bypass `GetReadablePage`'s factory copy).

### 5.6 Hygiene

- `FileStreamFactory.GetStream()`: `try { return new AesStream(...) } catch { stream.Dispose(); throw; }` (#2579).
- `TransactionMonitor.Dispose()`: dispose the `ThreadLocal<TransactionService>` slot.
- `MemoryCache.Dispose()`: drop all segments (helps the GC when a
  `LiteDatabase` is disposed but the object is still referenced by a
  container/DI scope).

### 5.7 Observability

`SELECT $ FROM $database` → `cache`:

```
limitBytes, allocatedBytes, segments, totalPages, freePages, readablePages,
writablePages, pinnedPages, evictedPages, releasedSegments, overflowSegments,
hits, misses
```

`transactions` additionally reports `perTransactionInitial` and
`globalBudget`. Nothing else in the public API changes.

### 5.8 Expression cache

Two independent fixes, both small:

- `Query.EQ/GT/GTE/LT/LTE/Not/Between/StartsWith/Contains/In` build
  parameterized expressions: `BsonExpression.Create($"{field} = @0", value)`.
  The source text becomes constant per field and operator, so the cache
  stays at the number of distinct *shapes*. Index selection keys on
  `expr.Right.IsValue` (no field references), which a parameter node
  satisfies, so query plans do not change.
- Bound the two static dictionaries: cap at 1,000 entries; when the cap is
  hit, clear the dictionary (it is a pure compile cache; the cost of a miss is
  one parse+compile). A cheaper policy than LRU and immune to the pathological
  case of every key being unique. Expose `compiledExpressions` in
  `$database`.

## 6. Tests and verification

Unit (`LiteDB.Tests/Internals/Cache_Tests.cs`, `MemoryCache` with tiny
segments):
- `Cache_NeverExceedsLimit_WhenPagesAreIdle`: read 10x limit distinct pages,
  release each; `TotalPages <= CachePages` throughout, `evictedPages > 0`.
- `Cache_ReleasesSegments_AfterPeak`: pin 4x limit pages (overflow), release
  all, `TrimToLimit()`; `TotalPages == CachePages` and `releasedSegments > 0`;
  every released segment's `byte[]` is collectable (WeakReference).
- `Cache_AllocatesFromMostPopulatedSegment`: after a peak and partial
  release, new allocations do not resurrect nearly-empty segments.
- `Cache_EvictRace_ReaderRetries`: 8 reader threads hammering one key while
  a thread evicts; no exception, `ShareCounter` never negative from a reader's
  point of view, content always the factory's.
- `Cache_ReferencedBit_ProtectsHotPages`: hot set of N pages survives a
  scan of 100N cold pages.
- Debug-mode poison: when a frame becomes free in `DEBUG || TESTING`, fill it
  with `0xFF`; any use-after-release in the engine then fails `ENSURE`
  checks immediately in the test suite instead of silently reading recycled
  content.

Engine (`LiteDB.Tests/Engine/`):
- `Scan_DoesNotGrowCacheBeyondLimit` (#2278, #2619): 300 MB file,
  `cache size=32MB`, `FindAll().Count()` twice; `allocatedBytes <= 32 MB +
  1 segment`, `pinnedPages == 0` afterwards.
- `OpenCursor_PinsAtMostPerTxBudget` (#2278 variant): half-consumed
  enumerator; `pinnedPages <= PerTxInitial`.
- `BulkWrite_ReusesFrames` (#1756): 200k inserts with updates/deletes;
  `segments <= limit / 1 MB` and `freePages` never exceeds one segment plus
  one spare.
- `MemoryDb_LogCapacityReleasedOnCheckpoint` (#2541).
- `TempStream_DeletedOnDispose` (#2056), `AesStream_FailureDisposesFile`
  (#2579).
- `QueryEq_DoesNotGrowExpressionCache` (#1688, #2421): 100,000 `Query.EQ`
  calls with distinct values; static cache count stays below the cap and the
  `Source` of the produced expression is constant.
- Existing `Transactions_Tests` safepoint tests keep passing; the test that
  sets `MaxTransactionSize` via reflection continues to work because the
  property is unchanged.

Benchmarks (`LiteDB.Benchmarks`): point lookups, full scan, bulk insert, with
`cache size` 8/64/256 MB against the current engine. Expected: no regression
for working sets under the limit; scans of files larger than the limit trade
LiteDB-cache hits for OS-page-cache reads (one syscall + 8 KB copy per page,
plus AES for encrypted files).

## 7. Rollout

| Phase | Change | Risk | Effect on the issue list |
|---|---|---|---|
| 1 (patch release) | `CacheSize` setting + connection string; budget derivation (5.3); recycle-when-at-limit fix in the existing `Extend()` (reuse idle pages whenever `TotalPages >= CachePages`, regardless of count); expression cache (5.8); counters; hygiene (5.6) | Low; no data structure change | Bounded growth. Scan ceiling drops from ~800 MB to ~limit + 8 MB. #2278, #2619, #2311, #1756 (growth part), #1688, #2421, #2395, #2579, #2056 |
| 2 (minor release) | `MemoryCache` v2 (5.2), trim at transaction end and checkpoint, segment sizing (5.4), `:memory:` log capacity + owned streams (5.5), `$database` fields (5.7) | Medium; core structure rewrite, covered by the tests in 6 | Memory returns after peaks. #1756 (shrink part), #2541, #1479, RC4 exception path |
| 3 (later) | Zero-copy page source for `MemoryStream`-backed databases | Medium | `:memory:` at ~1.2x raw size instead of ~3.6x |

Default `CacheSize` is the one behaviour change users can notice: databases
larger than 64 MB no longer end up fully cached. Anyone who wants the old
behaviour sets `cache size=1GB`.

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
  it caps the *growth* (Phase 1) but nothing shrinks without segment
  tracking (Phase 2).
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
- It makes the limit soft and ties the transaction budget to it (5.3) instead
  of throwing (#2624). A limit that can be exceeded only by pinned pages is
  enforceable; a hard limit that ignores pins is not.
- It tracks segments as first-class objects with per-segment free lists so
  that releasing a segment is a real operation (5.2), which is the piece
  #2649 attempted without segment liveness and therefore could not deliver.
- It takes #2649's eviction flag idea but folds it into `ShareCounter` as a
  `-2` sentinel that the *reader* path checks (5.2), so it actually protects
  something; #2649's flag was only ever read by the cleanup that set it.
- For the expression cache it removes the cause rather than adding a timer
  (5.8): parameterized `Query.*` helpers make the cache key constant, and a
  size cap with clear-on-overflow bounds the remaining growth. Regarding the
  maintainer's doubt on #2438: delegates produced by `Expression.Compile()`
  are backed by collectible dynamic methods, so dropping the last reference
  does free them; with parameterization the question mostly disappears
  because far fewer delegates are created.
- Two suggestions from the #2649 review are taken as-is: `Interlocked`
  counters instead of `ConcurrentQueue.Count`, and a configurable object
  rather than an enum for tuning. The timing-wheel idea mentioned there is
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
            line += $"  cache: segs={c["extendSegments"].AsInt32,3} pages={c["extendPages"].AsInt32,6} ({c["extendPages"].AsInt32 * 8 / 1024,4} MB) free={c["freePages"].AsInt32,6} readable={c["readablePages"].AsInt32,6} writable={c["writablePages"].AsInt32,5} inUse={c["pagesInUse"].AsInt32,5}  tx: open={t["open"].AsInt32} avail={t["availableSize"].AsInt32}  log={info["logFileSize"].AsInt32 / 1024 / 1024} MB";
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
