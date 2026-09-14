# LiteDB code audit — findings ranked by severity

Automated multi-agent static audit of the `dev` branch, with independent adversarial
verification and a manual re-check of the highest-severity findings.

| | |
|---|---|
| Commit audited | `a50661a` (branch `dev`) |
| Scope | `LiteDB/` core library (~40k LOC) plus `LiteDB.Shell`, `LiteDB.Stress`, `LiteDB.ReproRunner`, `LiteDB.Demo.Tools.VectorSearch`, `scripts/`, `.github/` |
| Finder agents | 29 (23 subsystem-scoped + 3 repo-wide sweeps + 3 new-feature-focused) |
| Verifier agents | 376 (1–3 per finding, each assigned a distinct refutation lens) |
| Raw findings | 233 |
| Survived verification | 203 |
| After dedup | **170 unique defects** (33 duplicates folded in) |

| Severity | Count |
|---|---|
| critical | 7 |
| high | 57 |
| medium | 75 |
| low | 31 |

## How much to trust this

Read this section before acting on anything below.

- **Nothing was compiled or executed.** No .NET SDK is available in the audit environment, so every
  finding is static reasoning over source. No finding has a passing/failing test attached.
- **The automated verification pass was weak.** 203 of 206 findings were labelled "confirmed" by the
  adversarial verifiers — a 98.5% confirm rate. Verifiers instructed to refute that refute 1.5% of the
  time are not discriminating. Treat the *automated* confirmation as near-worthless signal.
- **What that means in practice:** the 15 findings marked ✅ below were re-checked by hand against the
  real source, and all 15 held up. The rest are plausible and specific but unverified beyond the
  automated pass. Confirm before you fix.
- **One finding has runnable proof.** The `SqlLike` defects were verified by porting the function
  verbatim and brute-forcing it (C5, plus the appendix).
- **Known coverage gaps** are listed at the end. 27 lower-ranked findings past the per-finder cap were
  never verified and are excluded entirely.

## Critical

Seven unique critical defects. All but one were hand-verified.

### C1. Committed WAL pages are acknowledged without an fsync of the log file ✅

`LiteDB/Engine/Disk/DiskService.cs:244` · durability · calibrated rank #1
 · **hand-verified**

**Mechanism.** `WriteLogDisk` is the only path that persists a transaction's pages, and it ends with `stream.Flush()` inside the writer lock. For the log writer that stream is a plain `FileStream` created by `FileStreamFactory.GetStream(true, true)` with `FileOptions.SequentialScan` only - no `WriteThrough` (LiteDB/Engine/Disk/StreamFactory/FileStreamFactory.cs:47-63, LiteDB/Engine/Disk/StreamFactory/StreamPool.cs:26). `Stream.Flush()` on a `FileStream` is `Flush(flushToDisk: false)`: it pushes the managed buffer into the OS page cache and does not issue an fsync. The codebase has a dedicated `FlushToDisk()` extension that calls `FileStream.Flush(true)` (LiteDB/Utils/Extensions/StreamExtensions.cs:13-30) and uses it for the data file (`WriteDataDisk` line 359, `Initialize` line 113, `PromoteVectorFormat`, `MarkAsInvalidState`) - but never for the log write path. `TransactionService.Commit` calls `_disk.WriteLogDisk(...)` and then immediately `_walIndex.ConfirmTransaction(...)` (LiteDB/Engine/Services/TransactionService.cs:265-272), after which the commit returns success to the caller. So the transaction is acked while its bytes exist only in the OS page cache. Nothing later fsyncs the log either: the only log fsync-adjacent operation is `Clear()`'s `SetLength(0)` during checkpoint, which discards the log.

**Failure.** Default pragmas (`CHECKPOINT = 1000` pages). Insert and commit 100 documents; each `Commit()` returns true, so the application treats them as durable. The log is ~a few pages, far below the 1000-page auto-checkpoint threshold, so no `FlushToDisk` ever runs. Now cut power (or `echo b > /proc/sysrq-trigger`). On restart, `LiteEngine.Open` -> `WalIndexService.RestoreIndex` reads the log file, which the OS never persisted; the confirmed pages are simply absent and the data file still holds the pre-commit state. Up to 1000 pages' worth of acknowledged commits are silently gone - the classic durability violation the WAL exists to prevent.

**Suggested fix.** Replace `stream.Flush()` at the end of `WriteLogDisk` with `stream.FlushToDisk()` so the log is fsynced before `ConfirmTransaction` publishes the versions and the commit is acked. If the fsync-per-commit cost is unacceptable, make it an explicit pragma (e.g. a `synchronous` setting) rather than the silent default.


> **Context on C1.** `git log -L` on this line shows the plain `Flush()` predates this fork — it is
> long-standing upstream LiteDB behaviour, not a regression introduced here. It still meets the
> rubric's definition of critical (an acked write can be lost on power loss), but the decision is
> arguably "undocumented durability tradeoff" rather than "bug": the fix costs an fsync per commit.
> Treat it as a design question to settle and document, and note that the data-file path
> (`DiskService.cs:359`) and the init path (`:113`) *do* call `FlushToDisk()` — only the log write
> path does not.

### C2. Rebuild(RebuildOptions) ignores the engine's own password, rewriting an encrypted database as plaintext on disk ✅

`LiteDB/Engine/Engine/Rebuild.cs:29` · security · calibrated rank #2
 · **hand-verified**

**Mechanism.** `LiteEngine.Rebuild(RebuildOptions)` forwards `options` verbatim and never falls back to `_settings.Password`. `RebuildOptions.Password` defaults to `null` (LiteDB/Engine/Structures/RebuildOptions.cs:22), and the public client entry point manufactures exactly such an object: `LiteDatabase.Rebuild(RebuildOptions options = null)` calls `_engine.Rebuild(options ?? new RebuildOptions())` (LiteDB/Client/Database/LiteDatabase.cs:304). Inside `RebuildService.Rebuild` the *reader* is built from `_settings` (so it decrypts correctly) but the destination engine is built from the options: `new LiteEngine(new EngineSettings { Filename = tempFilename, Collation = options.Collation, Password = options.Password })` (LiteDB/Engine/Services/RebuildService.cs:53-58). With `Password == null`, `EngineSettings.CreateDataFactory` returns a `FileStreamFactory` with no `AesStream`, so every page of the rebuilt database is written unencrypted. `RebuildService` then renames the original encrypted file to `-backup` and moves the plaintext temp file into `_settings.Filename`. The note-only `LiteEngine.Rebuild()` overload at line 42-48 does pass `_settings.Password`, which shows the pass-through overload is missing the same defense. Collation is broken the same way: `options.Collation` is also `null` by default, so the rebuilt file silently gets `Collation.Default` instead of the source database's collation (RebuildService copies CHECKPOINT/TIMEOUT/LIMIT_SIZE/UTC_DATE/USER_VERSION pragmas but never COLLATION).

**Failure.** `using var db = new LiteDatabase("Filename=data.db;Password=secret"); db.Insert(...); db.Rebuild();` -> `data.db` on disk is now a fully readable, unencrypted LiteDB file containing all previously encrypted documents (the encrypted original survives only as `data-backup.db`). `LiteEngine.Rebuild` then calls `this.Open()`, which builds an `AesStream` over the now-plaintext file; `isEncrypted != 1` so `AesStream` throws `LiteException.FileNotEncrypted()`, `Open()` catches, calls `Close(ex)` and rethrows. Net result: confidential data written to disk in the clear AND the application can no longer open its own database. The same call on a database created with a custom collation (e.g. `pt-BR/IgnoreCase`) silently rebuilds it as `Collation.Default`, and a connection string that pins `collation=pt-BR/IgnoreCase` then fails on open with "Datafile collation ... is different from engine settings".

**Suggested fix.** In `LiteEngine.Rebuild(RebuildOptions)`, default the options from the live engine before handing them to `RebuildService`: `options.Password ??= _settings.Password;` and `options.Collation ??= _header.Pragmas.Collation;` (or make `RebuildService` fall back to `_settings` for both). Have `LiteDatabase.Rebuild(null)` route to the parameterless `LiteEngine.Rebuild()` overload, which already does this.

Also reported independently as: `LiteDB/Client/SqlParser/Commands/Rebuild.cs:18` (sqlparser)

### C3. FileReaderV8.Open() swallows every non-IO exception, so a partially readable file rebuilds into an EMPTY database ✅

`LiteDB/Engine/FileReader/FileReaderV8.cs:88` · durability · calibrated rank #4
 · **hand-verified**

**Mechanism.** Open() wraps LoadIndexMap/LoadPragmas/LoadDataPages/LoadCollections/LoadIndexes in one try/catch. HandleError() only rethrows when `ex is IOException`; LiteException (LiteException : Exception, see LiteDB/Utils/LiteException.cs:13) and every other type are recorded in _errors and discarded. LoadDataPages (line 279-282) does `ReadPage(0).GetValue()` (Result<T>.GetValue rethrows on failure) and `ENSURE(lastPageID <= _maxPageID, ...)`, and ENSURE is NOT [Conditional("DEBUG")] (LiteDB/Utils/Constants.cs:137-159) so it throws LiteException.InvalidDatafileState in release builds. When that fires, the remaining three loaders never run, _collections/_collectionsDataPages/_indexes stay empty, and Open() returns as if it succeeded. RebuildService.Rebuild() (LiteDB/Engine/Services/RebuildService.cs:52-105) then calls engine.RebuildContent(reader), which iterates reader.GetCollections() -> nothing, and afterwards unconditionally renames the source file to '-backup' and installs the empty temp file as the live database. No exception reaches the caller and Rebuild() returns a normal byte-difference value.

**Failure.** A 1000-page (8 MB) v8 data file is truncated to 500 pages (interrupted copy / full disk / fs corruption). The user calls db.Rebuild() (or opens with auto-rebuild=true after the invalid-state flag is set, LiteDB/Engine/LiteEngine.cs:113). _maxPageID = 500, header P_LAST_PAGE_ID = 999, so ENSURE(999 <= 500) throws inside LoadDataPages; Open() catches it, records one FileReaderError and returns. RebuildContent copies zero collections, the original file is moved to 'db-backup.db' and an empty database replaces 'db.db'. Every collection and document disappears from the application's view, and Rebuild() reports success instead of throwing - even though ~500 pages of data were still readable page-by-page (each individual ReadPage failure is already tolerated by design).

**Suggested fix.** Do not treat the loader phase as best-effort as a whole: make LoadDataPages tolerant (clamp the scan to _maxPageID instead of ENSURE-ing on lastPageID, and scan all pages as the TODO on line 280 suggests), and let Open() rethrow when it produced no usable state (e.g. header unreadable / zero collections found) so RebuildService aborts before renaming the source file.

### C4. OpenDatabase() acquires the cross-process mutex unconditionally but callers only release it when it returned true, so every nested/in-transaction operation permanently leaks one mutex recursion level ✅

`LiteDB/Client/Shared/SharedEngine.cs:48` · concurrency · calibrated rank #5
 · **hand-verified**

**Mechanism.** `OpenDatabase()` calls `_mutex.WaitOne()` *before* testing `!_transactionRunning && _engine == null`. A Win32/PAL named mutex is recursive: each successful WaitOne increments the owner's lock count and requires a matching `ReleaseMutex()`. When the engine is already open (nested query) or a transaction is running, OpenDatabase returns **false** while still holding an extra acquisition, and every caller (`QueryDatabase`, line 271, and `Query`, line 150) skips `CloseDatabase()` in that case (`if (opened)`). The extra acquisition is never released — not by `Commit`/`Rollback` (one release each) and not by `Dispose` (line 257, single release, and only when `_engine != null`). The mutex therefore stays owned by that thread for the rest of the process lifetime, and since `OpenDatabase` waits with no timeout, every other process/thread opening the same file in shared mode blocks forever.

**Failure.** Process A: `using var db = new LiteDatabase(new ConnectionString{Filename="a.db", Connection=ConnectionType.Shared}); db.BeginTrans(); db.GetCollection("c").Insert(doc); db.Commit(); db.Dispose();`. Counts: BeginTrans WaitOne -> 1 (engine created); Insert -> OpenDatabase WaitOne -> 2, returns false (transaction running), no CloseDatabase; Commit -> CloseDatabase -> 1. Dispose sees `_engine == null` and releases nothing. `Global\<name>.Mutex` is left owned at level 1 by that thread. Process B now runs any shared-mode operation on a.db and hangs forever inside `_mutex.WaitOne()` — the database file is wedged for every other process (and every other thread in A) until A exits. The same leak occurs with no transaction at all via nested access, which the repo's own tests exercise: `LiteDB.Tests/Engine/Recursion_Tests.cs` (`foreach (doc in collection.FindAll()) collection.Update(doc);`) leaks one level per Update and `LiteDB.Tests/Issues/Issue2534_Tests.cs` leaks three; they only pass because a recursive mutex is re-entrant for the *same* thread.

**Suggested fix.** Restore an explicit reference count: take the mutex only on the 0->1 transition (and release only on 1->0), or always release the extra acquisition before returning false (`_mutex.ReleaseMutex(); return false;`) so WaitOne/ReleaseMutex are balanced on every path.

Also reported independently as: `LiteDB/Client/Shared/SharedEngine.cs:48` (crosscut-resource)

### C5. SqlLike backtracking is unbounded: patterns with repeated '%' hang forever or throw IndexOutOfRangeException ✅

`LiteDB/Utils/Extensions/StringExtensions.cs:136` · correctness · calibrated rank #7
 · **hand-verified + runnable proof**

**Mechanism.** On a mismatch the matcher rewinds with `back = patternIndex - lastWildCard - 1; i -= back; patternIndex = lastWildCard;`. `lastWildCard` is the index of the FIRST '%' of a run, but the inner while loop at lines 72-75 advances `patternIndex` across the whole run of k consecutive '%' without consuming any input character. So `back` over-counts by k-1 relative to the number of input characters actually consumed since the wildcard. Resetting patternIndex to lastWildCard (instead of past the run) then re-enters the same '%' branch. Net effect per cycle: i moves back by (k-1) more than it moved forward. For k = 2 the loop makes zero net progress (infinite loop); for k >= 3 i goes negative and `str[i]` on the next iteration throws IndexOutOfRangeException. Verified by re-implementing the function verbatim and running it: a brute-force sweep over all patterns of length <= 5 over {a,b,%,_} and all inputs of length <= 4 over {a,b} produced 688 non-terminating and 136 out-of-range cases. Reachable from any LIKE predicate: BsonExpressionOperators.LIKE (LiteDB/Document/Expression/Parser/BsonExpressionOperators.cs:162) for the non-indexed path and IndexLike.ExecuteLike/ExecuteStartsWith (LiteDB/Engine/Query/IndexQuery/IndexLike.cs:72,108,127) for the indexed path.

**Failure.** Hang: col.Insert(new BsonDocument{["_id"]=1,["Name"]="ab"}); then db.Execute("SELECT $ FROM col WHERE $.Name LIKE '%%a'") (or col.Find(Query.Where("$.Name LIKE '%%a'"))) -> SqlLike("ab", "%%a") spins forever on one CPU, inside an open read transaction, so the query never returns, the transaction is never released and engine Dispose blocks. Crash: the same document with pattern '%%%a' -> SqlLike("ab","%%%a") drives i to -1 and throws IndexOutOfRangeException out of query execution (an unhandled non-LiteException escaping the pipeline).

**Suggested fix.** Collapse runs of '%' when the pattern is first seen (or record the index just past the run in lastWildCard), and clamp the rewind: `i = Math.Max(-1, i - back)` is not enough on its own - the loop must be guaranteed to make forward progress, e.g. track the input position where the wildcard matched and resume from that position + 1.

### C6. Transaction ID is allocated before the transaction read lock is taken, so a concurrent Checkpoint() can reset the ID counter and hand the same transactionID to two transactions ✅

`LiteDB/Engine/Services/TransactionService.cs:70` · concurrency · calibrated rank #8
 · **hand-verified**

**Mechanism.** TransactionMonitor.GetTransaction constructs the TransactionService (which calls walIndex.NextTransactionID() at TransactionService.cs:70) at TransactionMonitor.cs:61, publishes it into the registry at line 65, and only then acquires the engine-wide transaction read lock at line 68. WalIndexService.CheckpointInternal runs under the exclusive write lock and finishes with Clear(), which does `_lastTransactionID = 0; _currentReadVersion = 0;` plus `_disk.SetLength(0, FileOrigin.Log)`. Clear() assumes no transaction ID is outstanding, which is guaranteed only for transactions that already hold the read lock. A thread that has taken its ID and is *blocked* in LockService.EnterTransaction (a waiting writer parks new readers on ReaderWriterLockSlim) keeps a pre-reset ID and then runs after the checkpoint with `_lastTransactionID` back at 0. The counter now re-issues that same value. WalIndexService.RestoreIndex even documents the hazard ("Reusing one would make its old pages appear committed") but Clear() does not honour it.

**Failure.** Engine has been running, `_lastTransactionID == 5`. Thread B calls db.Checkpoint() (LockService.EnterExclusive waits for the write lock). Thread A starts an insert: TransactionService ctor takes transactionID = 6, _transactions.Add succeeds, then A blocks in EnterTransaction. B gets the write lock, checkpoints, Clear() resets the counter to 0 and truncates the log, then exits. A resumes with ID 6, bulk-inserts, safepoints flush pages carrying TransactionID=6 into the log (unconfirmed), then hits a unique-index violation; AutoTransaction rolls back, leaving those pages in the log with ID 6 and no confirmation. Six later transactions commit; the sixth is assigned ID 6 and calls WalIndexService.ConfirmTransaction(6, ...), which adds 6 to `_confirmTransactions`. The next checkpoint (CheckpointInternal copies every log page whose transactionID is in `_confirmTransactions`) copies thread A's rolled-back pages into the data file -> silently committed garbage / on-disk corruption. The same ID collision also makes RestoreIndex after a crash fold A's uncommitted pages into the WAL index for the reused ID.

**Suggested fix.** Acquire the transaction read lock (or at least register intent under the same lock that Checkpoint takes exclusively) before calling NextTransactionID(), or make WalIndexService.Clear() preserve `_lastTransactionID` (monotonic, never reset) so IDs can never be reused.

### C7. IndexService.Find returns the head/tail sentinel node on an exact match, letting Delete destroy an index's tail node ✅

`LiteDB/Engine/Services/IndexService.cs:398` · corruption · calibrated rank #9
 · **hand-verified**

**Mechanism.** The skip list keeps two sentinels per index: head (key = BsonValue.MinValue) and tail (key = BsonValue.MaxValue). Every level's forward chain terminates at the tail (AddNode, line 139: `if (next.IsEmpty) next = index.Tail;`), and AddNode's scan explicitly refuses to step onto it (line 112: `right != index.Tail`). Find has no such guard: its walk compares the sentinel's key like any other key. Because BsonType.MaxValue.CompareTo(BsonType.MaxValue) == 0 (BsonValue.cs:576-579), searching for BsonValue.MaxValue walks to the tail and falls into the `diff == 0` branch above, returning the TAIL NODE as if it were a real index entry — note the sibling branch 8 lines above (line 394) does filter `IsMinValue || IsMaxValue`, so the omission here is an oversight. LiteEngine.Insert (Insert.cs:65) and LiteEngine.UpdateDocument (Update.cs:104) both reject Min/MaxValue ids up front, but LiteEngine.Delete (Delete.cs:204) does not: it feeds the id straight into Find and then calls `indexer.DeleteAll(pkNode.Position)`, which runs DeleteSingleNode on the returned sentinel — unlinking it from all 32 levels and freeing its page slot (IndexService.cs:277-298) while CollectionIndex.Tail still points at that address. The transaction commits normally, so the damage is durable.

**Failure.** `var col = db.GetCollection("test"); col.Delete(BsonValue.MaxValue);` (public API; only `id.IsNull` is rejected in Client/Database/Collections/Delete.cs:14). Find(pk, MaxValue, false, Ascending) walks level 0 from head, hits the tail (diff == 0) and returns it; data.Delete(PageAddress.Empty) is a no-op; DeleteAll sets head.Next[0] = Empty and frees the tail's page slot; Delete returns 1 ("deleted"), and the transaction commits the mangled index to disk. The next `col.Insert(new BsonDocument{["_id"]=1})` allocates the freed slot for the new node (BasePage.GetFreeIndex returns HighestIndex+1 = the tail's old index), so the new node's address equals index.Tail; AddNode then does `next = index.Tail` and `GetNode(next).SetPrev(level, node.Position)` on the node itself, producing Next[0] == Prev[0] == self. After that, FindAll yields the same document repeatedly until `ENSURE(counter++ < _maxItemsCount)` throws INVALID_DATAFILE_STATE, which EngineState.Handle turns into `_engine.Close(ex)` — the whole engine shuts down, and the corruption reproduces after reopening the file. If the slot is not reused, GetNode(index.Tail) instead fails `ENSURE(index <= this.HighestIndex)` in BasePage.Get, closing the engine on every subsequent insert or descending scan of that collection. A read-only variant needs no delete at all: `col.Find(Query.EQ("_id", BsonValue.MaxValue))` (Query.EQ serializes to `{"$maxValue":"1"}` -> MAXVALUE(), Fields.Count == 0 so IsValue is true and IndexEquals is chosen) yields the tail node, and DatafileLookup.Load's `ENSURE(node.DataBlock != PageAddress.Empty)` throws INVALID_DATAFILE_STATE -> engine Close, i.e. a query-triggered shutdown of the database.

**Suggested fix.** Treat the sentinels as sentinels inside Find: bound the walk with `right != index.Tail` (ascending) / `right != index.Head` (descending) exactly as AddNode does, and/or return null from the `diff == 0` branch when `rightNode.Key.IsMinValue || rightNode.Key.IsMaxValue`. Additionally reject `id.IsMinValue || id.IsMaxValue` in LiteEngine.Delete the way Insert/UpdateDocument already do.

Also reported independently as: `LiteDB/Engine/Services/IndexService.cs:403` (datastruct)

## High

57 unique high-severity defects.

### H1. Operator precedence table gives `-` lower precedence than `+` (and `*` lower than `/` and `%`), so `a - b + c` is evaluated as `a - (b + c)` ✅

`LiteDB/Document/Expression/Parser/BsonExpressionParser.cs:36` · correctness · rank #11

**Mechanism.** `ParseFullExpression` collects a flat list of operands/operators and then reduces them by scanning `_operators` in dictionary insertion order (line 135: `var op = _operators.ElementAt(order)`), reducing *all* occurrences of a higher-listed operator before any lower-listed one. Because `+` is inserted before `-` (lines 35-36) and `%`/`/` before `*` (lines 32-34), operators that must be left-associative at equal precedence are reordered: every `+` in the expression is folded before any `-`, regardless of position.

**Failure.** `BsonExpression.Create("10 - 5 + 3").ExecuteScalar()` builds values=[10,5,3], ops=["-","+"]; the loop finds "+" first (order=3, n=1), reduces 5+3=8, then reduces 10-8, returning **2** instead of 8. Likewise `SELECT total * qty % 10` evaluates `total * (qty % 10)` instead of `(total * qty) % 10` — e.g. `10 * 4 % 3` yields 10 instead of 1. Arithmetic in SELECT/UPDATE/WHERE and in index expressions silently produces wrong numbers; the existing test `S("a + 1 - c")` passes only because `+` happens to be the leftmost operator there.

**Fix.** Group operators into precedence *levels* and reduce each level strictly left-to-right (reduce the leftmost operator whose precedence equals the current level), instead of reducing by per-operator dictionary order.

### H2. SqlLike compares trailing input characters against a stale pattern character, matching strings longer than the pattern ✅

`LiteDB/Utils/Extensions/StringExtensions.cs:61` · correctness · rank #12

**Mechanism.** The loop iterates over `str`, not over `pattern`. When `patternIndex` has run off the end of the pattern, `endOfPattern` is set true and the `if (!endOfPattern)` block is skipped, so `p` is NOT updated - it keeps the last character it was assigned. The comparison branches below still run and compare the current input character against that stale `p`, and on a match increment `patternIndex` further. Consequently input characters beyond the pattern length are accepted whenever they happen to equal the pattern's last character, and the function returns `isMatch && endOfPattern` = true. There is no final check that the whole input was consumed by the pattern. Verified against a reference LIKE implementation (regex fullmatch with % -> .* and _ -> .) over all wildcard-free/single-% patterns of length <= 5 and inputs of length <= 4: 92 false positives, all of this shape.

**Failure.** A collection with documents Name = "Hawai" and Name = "Hawaii" and NO index on Name (so the predicate is evaluated by BsonExpressionOperators.LIKE at line 162). db.Execute("SELECT $ FROM col WHERE $.Name LIKE 'Hawai'") returns BOTH documents: SqlLike("Hawaii","Hawai") consumes 'H','a','w','a','i' (patternIndex -> 5 == pattern.Length) and then compares the extra 'i' against the stale p == 'i', matches, and returns true. Same for SqlLike("aa","a") and SqlLike("Johnn","John"). If the field IS indexed the optimizer takes IndexLike's `_equals` fast path and returns the correct single row, so indexed and non-indexed execution of the same query disagree - wrong results are returned as if correct.

**Fix.** When `endOfPattern` is true and no wildcard is pending, the match must fail (or backtrack) instead of reusing the previous `p`; and the final result must require that the input was fully consumed by the pattern, not just `isMatch && patternIndex >= pattern.Length`.

### H3. SqlLike treats '_' as a literal when it directly follows '%', so every pattern containing "%_" fails to match ✅

`LiteDB/Utils/Extensions/StringExtensions.cs:95` · correctness · rank #13

**Mechanism.** When a '%' turns `isWildCardOn` on, the scanner advances `patternIndex` past the '%' run and sets `p` to the next pattern character. If that character is '_', the `if (isWildCardOn)` branch is evaluated before any '_' handling and compares the input character against the literal '_' via collation.Compare. The '_' branch at line 86 does fire on the following iteration (setting isCharWildCardOn and advancing patternIndex) but `isWildCardOn` still has priority at line 93, so the single-character wildcard is consumed from the pattern without ever being honoured, and the wildcard search keeps looking for a literal '_' in the input. Since '_' is never present, patternIndex never passes the '_' position and the trailing-'%'-only check at lines 150-164 fails, so the function returns false. Verified against a reference regex LIKE over all patterns of length <= 5 over {a,b,%,_} (excluding '%%' to isolate this from the backtracking bug): 1364 false negatives, every one containing the two-character sequence "%_".

**Failure.** Document Name = "Smith", no index on Name. db.Execute("SELECT $ FROM col WHERE $.Name LIKE '%_mith'") returns zero rows; the correct answer is the "Smith" document (% matches the empty string, _ matches 'S'). Likewise SqlLike("aa", "%_a") and SqlLike("a", "%_") both return false instead of true - matching rows are silently dropped.

**Fix.** Handle '_' before the wildcard-search branch: when `isWildCardOn` is set and the next pattern character is '_', the '_' should consume one input character unconditionally and the wildcard search should resume against the character after it.

### H4. SqlLikeStartsWith reports hasMore=false for a pattern ending in '_', so indexed LIKE skips the wildcard check and returns extra rows

`LiteDB/Utils/Extensions/StringExtensions.cs:190` · correctness · rank #14

**Mechanism.** `hasMore = !(i == len || i == len - 1);` treats "the first wildcard is the last character of the pattern" as "the prefix alone decides the match". That is only true for '%'. For '_' the prefix is not sufficient: 'abc_' must match exactly one extra character. The loop at lines 178-188 breaks on either '%' or '_', so both land in the same branch. IndexLike consumes this as `_testSqlLike` (IndexLike.cs:19) and, when it is false, yields every node that passes only `valueString.StartsWith(_startsWith, OrdinalIgnoreCase)` without ever calling SqlLike (IndexLike.cs:71-75 and 106-111). `_equals` is false here because _pattern ("abc_") != _startsWith ("abc"), so the stricter Equals path is not taken either.

**Failure.** col.EnsureIndex(x => x.Name); insert Name = "abc", "abcd", "abcde". Query `$.Name LIKE 'abc_'` (IndexCost.cs:87 builds IndexLike for a LIKE predicate on an indexed field). Expected: only "abcd". Actual: "abc", "abcd" and "abcde" are all returned, because _startsWith == "abc" and _testSqlLike == false suppress the per-row SqlLike test. The same query on a non-indexed field goes through BsonExpressionOperators.LIKE -> SqlLike and returns only "abcd", so adding an index silently changes the result set.

**Fix.** Only treat a trailing wildcard as 'nothing more to test' when that wildcard is '%': e.g. `hasMore = !(i == len || (i == len - 1 && str[i] == '%'));`

### H5. StartsWith/Contains/EndsWith inject the raw search string into a LIKE pattern, so '%' and '_' in user input act as wildcards ✅

`LiteDB/Client/Mapper/Linq/TypeResolver/StringResolver.cs:34` · correctness · rank #15

**Mechanism.** The three patterns build a SQL LIKE pattern by concatenating the caller's argument with `%`: `# LIKE (@0 + '%')`. At runtime BsonExpressionOperators.LIKE (BsonExpressionOperators.cs:157-168) calls StringExtensions.SqlLike, which treats `%` as a multi-character wildcard and `_` as a single-character wildcard (Utils/Extensions/StringExtensions.cs:66-90). Because the argument is passed through untouched (VisitConstant stores it verbatim as a parameter and the `+` concatenation happens at evaluation time), any wildcard character inside the user's search term becomes an active wildcard. SqlLike supports no escape character at all, so a caller cannot defend against this.

**Failure.** A search box passes its text into `col.Find(x => x.Name.Contains(term))`. With term = `50%`, the generated expression is `$.Name LIKE ('%' + @p0 + '%')` with p0 = `50%`, i.e. pattern `%50%%`, which matches "5012 Main St" and "501" — documents that do not contain the literal substring "50%". Likewise `x => x.Code.StartsWith("A_")` matches "AB123" (pattern `A_%`), and `x => x.Email.EndsWith("_test.com")` matches "joe@xtest.com". The wrong rows are returned as if correct.

**Fix.** Do not route these methods through LIKE with a raw argument. Either add wildcard escaping support to SqlLike and escape the parameter, or translate to non-pattern primitives (e.g. `INDEXOF(#, @0) = 0` for StartsWith, `INDEXOF(#, @0) >= 0` for Contains, and a SUBSTRING/LENGTH comparison for EndsWith).

### H6. BsonDocument.CompareTo is not antisymmetric: two documents with disjoint key sets each compare greater than the other, corrupting skip-list index order

`LiteDB/Document/BsonDocument.cs:82` · correctness · rank #16

**Mechanism.** CompareTo iterates only `this`'s own keys and looks each one up in `other`. A key present in `this` but absent from `other` yields `other[key] == BsonValue.Null`, and any non-null value compares greater than Null (BsonType.Null = 1 is the lowest non-MinValue sort order). Because the loop is driven by `this`'s keys, the same reasoning applies symmetrically, so both directions return +1. The comparator therefore violates antisymmetry (sgn(a.CompareTo(b)) == -sgn(b.CompareTo(a))), which IndexService's skip list relies on to keep nodes sorted. Documents are legal index keys — IndexService.AddNode only rejects MinValue/MaxValue (LiteDB/Engine/Services/IndexService.cs:67) and BufferSliceExtensions.WriteIndexKey/ReadIndexKey explicitly serialize BsonType.Document (LiteDB/Utils/Extensions/BufferSliceExtensions.cs:172, 362).

**Failure.** Collection `c` with `EnsureIndex("meta", "$.meta")` (non-unique). Insert `{_id:1, meta:{a:1}}` then `{_id:2, meta:{b:1}}`. In IndexService.AddNode (IndexService.cs:119) `diff = rightNode.Key.CompareTo(key, _collation)` evaluates `{a:1}.CompareTo({b:1})`: thisKeys=["a"], `{a:1}["a"]`=Int32(1) vs `{b:1}["a"]`=Null -> Type 2 vs Type 1 -> +1, so `diff == 1` breaks and `{b:1}` is linked BEFORE `{a:1}`. Index level-0 order is now head -> {b:1} -> {a:1} -> tail. Now run `SELECT $ FROM c WHERE meta = {a:1}`: IndexEquals calls IndexService.Find (IndexService.cs:387), which at level 0 evaluates `{b:1}.CompareTo({a:1})` = +1 == order, `!sibling` -> `break`, the level loop exhausts and Find returns null. IndexEquals then `yield break`s and the query returns ZERO rows even though document _id=1 exists and matches. The mis-ordered node links are persisted to the index pages, so the wrong answer is durable. The same inconsistency makes `buffer.Sort((l,r) => l.Key.CompareTo(r.Key, collation))` in GroupByPipe.cs:264 able to throw "IComparer.Compare() method returns inconsistent results".

**Fix.** Compare the union of both key sets in a deterministic (e.g. ordinal-sorted) order, or compare key names first and then values, so the relation is antisymmetric and transitive. E.g. sort both key arrays, compare key names pairwise, and only compare values for matching names.

### H7. Compiled-expression cache keyed only on the source string reuses delegates that have an older sub-expression (and its parameter document) baked in as a constant

`LiteDB/Document/Expression/BsonExpression.cs:373` · correctness · rank #17

**Mechanism.** Array-filter/array-index and MAP/FILTER/SORT nodes embed the *instance* of their nested BsonExpression into the LINQ tree via `Expression.Constant(inner)` (BsonExpressionParser.cs:1162, BsonExpressionParser.Functions.cs:52, BsonExpressionParser.cs:738/1071). At run time the operator ignores the `parameters` lambda argument and calls `filterExpr.ExecuteScalar(...)`, which uses `filterExpr.Parameters` — the BsonDocument captured when that inner expression was *parsed* (BsonExpressionOperators.cs:328, BsonExpressionFunctions.cs:64/78/93). `Compile` looks the outer expression up in the process-wide `_compiledCache` using only `expr.Source`, so the second and every later parse of the same source text throws away the freshly built tree (with the fresh inner instance and fresh parameter document) and installs the *first* compilation, which still points at the first inner instance. `BsonExpression.SetParameters` exists but is never called anywhere, so nothing re-points the baked constant.

**Failure.** Process-wide, single-threaded: `BsonExpression.Create("ARRAY($.items[@.price > @0])", 10).ExecuteScalar(doc)` returns items with price>10 and caches the delegate under key `ARRAY($.items[@.price>@0])`. Next, `BsonExpression.Create("ARRAY($.items[@.price > @0])", 100).ExecuteScalar(doc)` hits that cache entry; ARRAY_FILTER executes the *first* inner expression whose Parameters is `{0:10}`, so the second call also returns every item with price>10 instead of >100 — no exception, silently wrong results. The same happens for `db.Execute(sql, parameters)` (each Execute builds its own parameters BsonDocument) and across different LiteDatabase instances in one process, so one caller's parameter values leak into another caller's query. If the stale expression is used in `UPDATE col SET items = ARRAY(items[@.id != @0])`, the wrong array elements are written to disk.

**Fix.** Do not cache (or do not reuse the cache for) expressions whose tree embeds a BsonExpression constant, or make nested expressions parameter-free by passing the runtime `parameters` argument down (ARRAY_FILTER/MAP/FILTER/SORT already receive it — call `inner.Execute(source, root, current, collation, parameters)` instead of relying on `inner.Parameters`). Alternatively include a marker in the cache key when the tree contains baked BsonExpression constants.

### H8. Index-consumed `!=` predicate is evaluated with binary collation, so the dropped filter changes query results

`LiteDB/Engine/Query/QueryOptimization.cs:229` · correctness · rank #18

**Mechanism.** The term chosen as the index is removed from the filter list, so the index scan must implement the predicate exactly. For BsonExpressionType.NotEqual, IndexCost.CreateIndex (Structures/IndexCost.cs:92) builds `new IndexScan(name, x => x.CompareTo(value) != 0, ...)` using the single-argument BsonValue.CompareTo, which hardcodes Collation.Binary (BsonValue.cs:549-552). The equivalent non-index evaluation uses BsonExpressionOperators.NEQ (Parser/BsonExpressionOperators.cs:151), i.e. `!collation.Equals(left, right)` with the database collation, whose default is CurrentCulture + CompareOptions.IgnoreCase (Collation.cs:44). The two disagree on any pair of strings that are collation-equal but not byte-equal, and since the filter was dropped nothing re-checks the predicate.

**Failure.** Default collation (culture/IgnoreCase). col.EnsureIndex(x => x.Name); insert {Name:"john"} and {Name:"mary"}. `col.Find(x => x.Name != "JOHN")` -> ChooseIndex picks the Name index (IndexScan, cost 80 < IndexAll 100), the term is consumed, IndexScan's predicate does an ordinal compare of "john" vs "JOHN" (non-zero) and yields the node -> {Name:"john"} is returned even though `Name != "JOHN"` is false under the database collation. Dropping the index (or adding any cheaper indexable term so `!=` stays a filter) returns only {Name:"mary"}.

**Fix.** Pass the collation into the IndexScan predicate (x => x.CompareTo(value, collation) != 0), or keep the `!=` term in _queryPlan.Filters when the index can only approximate it.

### H9. IndexRange duplicate-group backward walk compares keys with binary collation and silently drops matching documents

`LiteDB/Engine/Query/IndexQuery/IndexRange.cs:55` · correctness · rank #19

**Mechanism.** IndexService.Find is documented to return an arbitrary member of a duplicate-key group (IndexService.cs:367-371), so IndexRange must walk backwards to collect the group members that precede `first`. That walk uses the single-argument BsonValue.CompareTo, which hardcodes Collation.Binary (BsonValue.cs:549-552), while every other comparison in the same method (lines 68 and 84) uses indexer.Collation. Under the default culture/IgnoreCase collation, keys that are collation-equal but not byte-equal (e.g. "Ana" and "ANA") are one duplicate group in the index, but the backward walk stops at the first byte-different key, so those earlier group members are never yielded. The range term was consumed as the index term (QueryOptimization.cs:229), so no residual filter recovers them.

**Failure.** Default collation. col.EnsureIndex("name", "$.name") (non-unique); insert {name:"Ana"}, {name:"ANA"}, {name:"Bob"}. `WHERE name >= 'ana'` builds IndexRange("ana", MaxValue, startEquals:true, Ascending). When Find lands on the "ANA" node (which node is returned depends on the random skip-list levels, so this reproduces intermittently), the backward walk evaluates "Ana".CompareTo("ana") with Collation.Binary, gets non-zero, and exits without yielding; the forward loops then continue from "ANA". Result: 2 documents instead of 3 - {name:"Ana"} is silently lost even though it satisfies the predicate under the database collation.

**Fix.** Use `.Key.CompareTo(start, indexer.Collation) == 0` in the backward walk, matching lines 68 and 84.

### H10. Relational operators (> >= < <=) hardcode Collation.Binary, so range predicates ignore the database collation and disagree with index range scans

`LiteDB/Document/BsonValue.cs:551` · correctness · rank #20

**Mechanism.** `CompareTo(BsonValue)` unconditionally delegates with `Collation.Binary` (Invariant/Ordinal), and `operator >`, `>=`, `<`, `<=` (BsonValue.cs:648-666) all call that overload. The expression engine routes SQL/LINQ range predicates straight through these operators: BsonExpressionParser.cs:46-49 maps `>`,`>=`,`<`,`<=` to `GT`,`GTE`,`LT`,`LTE`, and BsonExpressionOperators.cs:122/129/137/144 implement them as `left > right` / `left <= right` (LTE even accepts a Collation parameter and discards it). Meanwhile `=`/`!=` go through `collation.Equals` and index range scans use the configured collation (IndexRange.cs:68,84 and IndexService.Find). The default pragma collation is `Collation.Default` = CurrentCulture + CompareOptions.IgnoreCase (Collation.cs:44, EnginePragmas.cs:39), whose string ordering differs in sign from ordinal.

**Failure.** Database created with the default collation (e.g. en-US/IgnoreCase). Collection `c` holds `{_id:1, name:"a"}`. Run `SELECT $ FROM c WHERE name > 'B'` with no index on `name`: the predicate is evaluated as a residual filter -> GT -> `operator >` -> Collation.Binary -> ordinal compare of 'a'(0x61) vs 'B'(0x42) -> +1 -> the document IS returned. Now `EnsureIndex("name")` and run the identical query: QueryOptimization picks IndexRange, which compares with `indexer.Collation` (culture, IgnoreCase) -> "a" < "b" -> the document is NOT returned. Same data, same query, opposite answers depending only on whether an index exists; and `WHERE name > 'B'` contradicts `ORDER BY name`, which is collation-aware.

**Fix.** Make the relational operators take the ambient collation (or remove them from the expression pipeline) and give GT/GTE/LT/LTE collation-aware implementations, e.g. `collation.Compare(left, right) > 0`, matching EQ/NEQ and IndexRange.

### H11. LIKE index range scan matches non-string index keys, producing documents the LIKE operator rejects

`LiteDB/Engine/Query/IndexQuery/IndexLike.cs:96` · correctness · rank #21

**Mechanism.** Both scan loops (lines 61-68 and 96-103) coerce non-string index keys with `node.Key.ToString()` (JSON serialization) and then prefix-match the coerced text. BsonExpressionOperators.LIKE (Parser/BsonExpressionOperators.cs:158-168) returns false unless BOTH sides are strings, so the index scan is strictly more permissive than the predicate it replaces. Because the LIKE term is consumed as the selected index term (QueryOptimization.cs:229) there is no residual filter. Numeric keys are reachable: BsonType.Int32 (2) sorts before BsonType.String (6) (BsonType.cs), so numeric nodes sit immediately before the first string node and are visited by the backward loop that starts at `first`.

**Failure.** col.EnsureIndex("name", "$.name"); insert {name:"1a"} and {name:123}. `db.Execute("SELECT $ FROM col WHERE name LIKE '1%'")` -> IndexLike seeks the first key >= "1" (the string "1a"), yields it, then steps backwards to the Int32 key 123, computes valueString "123", which StartsWith("1"), and yields that node too (SqlLikeStartsWith reports hasMore == false for "1%", so no re-test happens). The document {name:123} is returned although `123 LIKE '1%'` evaluates to false; dropping the index returns only {name:"1a"}.

**Fix.** Skip nodes whose key is not a string (mirror the `node.Key.IsString` guard already used in ExecuteLike, IndexLike.cs:127) instead of coercing keys with ToString().

### H12. ORDER BY DESC on an indexed field combined with LIKE 'prefix%' returns an empty result set

`LiteDB/Engine/Query/IndexQuery/IndexLike.cs:42` · correctness · rank #22

**Mechanism.** ExecuteStartsWith assumes it is positioned at the lowest key carrying the prefix, then walks the duplicate group backwards and the remaining range forwards. That only holds for Order == Ascending. QueryOptimization.DefineOrderBy (QueryOptimization.cs:345-352) sets `_queryPlan.Index.Order = orderBy.PrimaryOrder` whenever the ORDER BY primary expression equals the index expression and, with a single segment, also sets `orderBy = null` so no sorter runs. With Order == Descending (-1), IndexService.Find (IndexService.cs:372-408) walks from the tail and for sibling:true returns the greatest key strictly LESS than the search value (or null when none exists, since the head key is MinValue). A prefix string is smaller than every key that starts with it, so `first` never carries the prefix: the backward loop (lines 57-80) breaks on its first iteration and the forward loop (lines 87-120) walks further away from the matching range and breaks too. The LIKE term was already removed from _queryPlan.Filters at QueryOptimization.cs:229, so nothing re-checks the predicate or re-sorts.

**Failure.** col.EnsureIndex(x => x.Name) (index expression "$.Name"); insert {Name:"alpha"},{Name:"amber"},{Name:"beta"}. Run col.Query().Where(x => x.Name.StartsWith("a")).OrderByDescending(x => x.Name).ToList(). StringResolver turns StartsWith into `$.Name LIKE (@p0 + '%')`, IndexLike (cost 10) beats IndexAll (cost 100), DefineOrderBy flips Index.Order to -1 and drops the sorter. Find("a", sibling, desc) walks past "alpha"/"amber"/"beta" (all > "a"), reaches the head node, and returns null -> zero documents returned instead of [amber, alpha]. The same query without ORDER BY DESC, or with no index on Name, returns both documents.

**Fix.** Make ExecuteStartsWith direction-aware (seek the last prefix-matching key when Order is descending, e.g. by scanning ascending and reversing, or by seeking with the prefix's upper bound), or refuse to reuse the index ordering in DefineOrderBy when the chosen Index cannot honour a descending Order.

### H13. ORDER BY is silently dropped for IN predicates, but IndexIn returns nodes in the IN-list order

`LiteDB/Engine/Query/QueryOptimization.cs:345` · correctness · rank #23

**Mechanism.** DefineOrderBy assumes every Index implementation emits keys in this.Order when its expression matches the ORDER BY expression, and therefore removes the sorter entirely (orderBy = null -> QueryPipe.cs:46 skips OrderBy). IndexIn.Execute (IndexIn.cs:30-41) ignores this.Order completely: it iterates `_values.Distinct()` in the literal/parameter array order and runs an IndexEquals seek per value, so output is grouped by IN-list position, not sorted by key. IndexCost.CreateIndex (Structures/IndexCost.cs:93-95) builds IndexIn for BsonExpressionType.In, and the term is consumed (QueryOptimization.cs:229), so no later stage restores ordering.

**Failure.** col.EnsureIndex(x => x.Code); docs with Code 1,2,3. `col.Query().Where("$.Code IN [3,1,2]").OrderBy("$.Code").ToList()` -> ChooseIndex matches the Code index (IndexIn, cost 30 < IndexAll 100), DefineOrderBy sees "$.Code" == IndexExpression and nulls the sorter, IndexIn seeks 3 then 1 then 2 -> results returned as [3,1,2] while the caller asked for ascending order. Identical for the very common LINQ shape `col.Query().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)`: OptimizeTerms (QueryOptimization.cs:128-134) rewrites `@p0 ANY = $._id` into `$._id IN ARRAY(@p0)`, so results come back in the caller's `ids` order, not by _id.

**Fix.** Only reuse the index ordering for index implementations that actually stream keys in Order (IndexAll, IndexRange, IndexScan, ascending IndexLike); keep the sorter for IndexIn (or make IndexIn sort/merge its seeks and honour Order).

### H14. Index chosen from the right-hand side for LIKE/IN builds a non-equivalent index query while the predicate is dropped

`LiteDB/Engine/Query/QueryOptimization.cs:275` · correctness · rank #24

**Mechanism.** ChooseIndex accepts an index that matches the RIGHT operand for any predicate type, on the assumption that the operator can be mirrored. IndexCost's normalisation switch (Structures/IndexCost.cs:39-58) only mirrors the four ordering operators; Like and In fall through the `default` branch and keep their original type, so CreateIndex builds `indexedField LIKE constant` for `constant LIKE indexedField`, and `IndexEquals(constant)` for `constant IN indexedField`. Neither is equivalent to the original predicate, and the term is removed from _queryPlan.Filters (line 229), so the wrong index answer is returned verbatim.

**Failure.** Pattern table: col.EnsureIndex("pattern", "$.pattern"); docs {pattern:"a%"}, {pattern:"abc"}, {pattern:"xyz"}. `db.Execute("SELECT $ FROM col WHERE @0 LIKE $.pattern", "abc")` should return {pattern:"a%"} and {pattern:"abc"} (LIKE evaluates left.SqlLike(right), so the document field is the pattern). ChooseIndex matches the pattern index via the right-hand clause, IndexCost builds IndexLike("abc") - a wildcard-free pattern, so _equals is true - and only {pattern:"abc"} is returned; {pattern:"a%"} is silently dropped. Same shape for `WHERE @0 IN $.tags` against an index created as "$.tags": IndexEquals(@0) is compared against the whole-array key, matches nothing, and the query returns zero rows although the IN operator would match every document whose tags contain the value.

**Fix.** Restrict the right-hand-side index match to symmetric/mirrorable predicate types (Equal, NotEqual, and the four ordering operators normalised by IndexCost) and skip Like/In/Between when the constant is on the left.

### H15. GROUP BY re-sorts the stream after DefineOrderBy has discarded the user's ORDER BY as "provided by the index"

`LiteDB/Engine/Query/QueryOptimization.cs:349` · correctness · rank #25

**Mechanism.** DefineOrderBy runs before DefineGroupBy and drops the sorter whenever the single ORDER BY segment matches the index expression, on the assumption that the index delivers the final ordering. For a GROUP BY query that assumption is invalid: DefineGroupBy (lines 374-382) creates its own groupOrderBy whenever the GROUP BY expression differs from the index expression, and GroupByPipe then sorts the source by the group key (GroupByPipe.cs:40-43), destroying the index order. GroupByPipe applies the user's ORDER BY separately, over the grouped projection, via OrderGroupedResult (GroupByPipe.cs:51-55) - but only `if (query.OrderBy != null)`, which DefineOrderBy has just set to null. The requested ordering is therefore never applied.

**Failure.** col.EnsureIndex(x => x.Age); `db.Execute("SELECT {k:@key, c:COUNT($._id)} FROM col WHERE $.Age > 10 GROUP BY $.Category ORDER BY $.Age")`. ChooseIndex selects the Age index (IndexRange, IndexExpression "$.Age"); DefineOrderBy sees "$.Age" == IndexExpression with one segment and sets _queryPlan.OrderBy = null; DefineGroupBy sees "$.Category" != "$.Age" and adds groupOrderBy on Category; GroupByPipe sorts by Category, groups, and returns groups in Category order with query.OrderBy == null, so ORDER BY $.Age is silently ignored. The same happens with no WHERE clause when only the ORDER BY field is indexed (ChooseIndex.cs fallback at lines 295-306 picks the orderBy index).

**Fix.** Do not consume/drop the ORDER BY in DefineOrderBy when _query.GroupBy != null unless the GROUP BY expression is the same index expression (i.e. when DefineGroupBy will not introduce its own groupOrderBy).

### H16. Undefined VECTOR_SIM (null) satisfies the `<= maxDistance` predicate, so WhereNear returns non-matching documents (the entire collection on a dimension mismatch)

`LiteDB/Client/Vector/LiteQueryable.Vector.cs:26` · correctness · rank #26

**Mechanism.** CreateVectorSimilarityFilter encodes the whole vector search as the BSON predicate `<field> VECTOR_SIM @0 <= @1`. BsonExpressionMethods.VECTOR_SIM (LiteDB/Document/Expression/Methods/Vector.cs lines 25, 50, 53) returns BsonValue.Null whenever the similarity is undefined: the two vectors have different lengths, the field is missing / not a vector, either vector has zero magnitude, or a component is NaN. The predicate is evaluated by LTE (LiteDB/Document/Expression/Parser/BsonExpressionOperators.cs:143) which is BsonValue.operator<= -> CompareTo; because BsonType.Null == 1 sorts below BsonType.Double == 4 (LiteDB/Document/BsonType.cs), `Null <= anyNumber` is TRUE. So 'similarity undefined' is treated as 'distance 0 / perfect match'. This is only reachable when no vector index is selected, because TrySelectVectorIndex (LiteDB/Engine/Query/QueryOptimization.Vector.cs:38) skips every index whose metadata.Dimensions != target.Length and then leaves the predicate in _queryPlan.Filters for BasePipe.Filter to evaluate. When an index IS selected the term is consumed and VectorIndexQuery.Scan/Load (LiteDB/Engine/Query/IndexQuery/VectorIndexQuery.cs lines 97 and 117) correctly drop rows with no score - so the indexed and unindexed paths disagree and the unindexed one is wrong. No dimension validation exists at the API boundary: ValidateVectorArguments (line 12) only rejects an empty target and a NaN threshold.

**Failure.** Collection with a 3-dimension index: col.EnsureIndex("emb", "$.Embedding", new VectorIndexOptions(3)); documents {Id=1,Embedding=[1,0,0]}, {Id=2,Embedding=[0,1,0]}. Caller passes a 2-element query vector (wrong embedding model): col.Query().WhereNear(x => x.Embedding, new[]{1f,0f}, 0.25).ToArray(). TrySelectVectorIndex rejects the index on the dimension check, the filter falls back to scalar VECTOR_SIM which returns Null for every document, and Null <= 0.25 is true -> BOTH documents are returned as 'within distance 0.25', with no error. The existing test WhereNear_FallsBack_WhenDimensionMismatch (LiteDB.Tests/Query/VectorIndex_Tests.cs:409) calls query.ToArray() but deliberately asserts nothing about the rows. Second scenario with matching dimensions and no index at all: documents {Id=1,Embedding=[1,0]}, {Id=2 /* no Embedding */}, {Id=3,Embedding=[0,0]}; WhereNear(x => x.Embedding, [1,0], 0.25) returns Ids 1, 2 and 3, whereas after EnsureIndex on the same data it returns only Id 1 (asserted by LiteDB.Tests/Query/Issue2881_UndefinedVector_Tests.cs:62). Adding or dropping an index silently changes which documents a WhereNear returns.

**Fix.** Do not rely on three-valued comparison for the threshold. Either (a) emit a predicate that is false when the similarity is undefined, e.g. `(<field> VECTOR_SIM @0) != null AND (<field> VECTOR_SIM @0) <= @1`, or (b) reject the call at the API boundary when a vector index exists on the expression and target.Length != index dimensions, instead of silently falling back.

### H17. TopKNear orders by VECTOR_SIM ascending, and null (undefined similarity) sorts first, so garbage documents fill the top-k in the unindexed fallback

`LiteDB/Client/Vector/LiteQueryable.Vector.cs:94` · correctness · rank #27

**Mechanism.** TopKNear expresses 'nearest k' as ORDER BY VECTOR_SIM(field, target) ASC + LIMIT k. When no vector index matches (no index created, or metadata.Dimensions != target.Length per QueryOptimization.Vector.cs:38), _vectorPrimaryOrderMatched stays false and the plan sorts on the raw scalar VECTOR_SIM value. That value is BsonValue.Null for every document whose similarity is undefined (missing field, non-vector value, zero-magnitude vector, NaN component, wrong length - Vector.cs lines 25/50/53). The sorter orders BsonValues by CompareTo, and BsonType.Null (1) is less than BsonType.Double (4), so Null is the SMALLEST key: undefined-similarity documents are ranked as the BEST matches and are returned before any real neighbour. With WithScore() they come back with Score == null and Metric == Cosine (LiteQueryable.Vector.cs:192, Engine/Query/Structures/VectorScoreProjection.cs:29-38). Again the indexed path behaves correctly (VectorIndexQuery only yields scored rows), so the two paths disagree.

**Failure.** No vector index on the collection. Documents: {Id=1,Embedding=[1,0]}, {Id=2 /* Embedding absent */}, {Id=3,Embedding=[0,0]}, {Id=4,Embedding=[0.99,0.01]}. col.Query().TopKNear(x => x.Embedding, new[]{1f,0f}, 2).ToArray() returns Ids 2 and 3 (both score null, sorted first) and omits the two genuinely nearest documents 1 and 4. TopKNearWithScore returns the same two rows with Score = null, presented as the top-2 nearest neighbours. The equivalent query after EnsureIndex("emb", "$.Embedding", new VectorIndexOptions(2)) returns Ids 1 and 4.

**Fix.** Make undefined similarity sort last rather than first, and/or exclude it: add a null-guard term to the query (e.g. ORDER BY IIF(VECTOR_SIM(f,@0) = null, MAXVALUE, VECTOR_SIM(f,@0))) or add `VECTOR_SIM(f,@0) != null` to the filter list whenever TopKNear falls back to the scalar path.

### H18. EnsureIndex silently ignores an upgrade from non-unique to unique, leaving the uniqueness constraint unenforced

`LiteDB/Engine/Engine/Index.cs:43` · api-contract · rank #28

**Mechanism.** When an index with the requested name already exists, EnsureIndex compares only current.Expression against expression.Source. The `unique` argument is never compared with current.Unique, and CollectionIndex.Unique is never updated. The method returns false ('already exists') and the stored index keeps its original uniqueness flag. Duplicate detection lives entirely in IndexService.AddNode (`if (diff == 0 && index.Unique) throw LiteException.IndexDuplicateKey(...)`, IndexService.cs:121), so with Unique == false no duplicate check ever runs. LiteCollection<T>.EnsureIndex (LiteDB/Client/Database/Collections/Index.cs:19-25) forwards `unique` straight through and adds no check of its own, so no higher layer catches this.

**Failure.** col.EnsureIndex("email", "$.email", unique: false) returns true. Later (e.g. an app schema upgrade) col.EnsureIndex("email", "$.email", unique: true) returns false with no exception, and the index stays non-unique. col.Insert({_id:1, email:"a@b"}) then col.Insert({_id:2, email:"a@b"}) both succeed instead of the second throwing IndexDuplicateKey. The application's uniqueness invariant is silently violated and duplicate rows accumulate; Rebuild does not repair them either.

**Fix.** When current != null, also compare current.Unique != unique and either throw LiteException.IndexAlreadyExist(name) (consistent with the expression-mismatch case) or drop and rebuild the index with the requested uniqueness.

### H19. SUM/AVG accumulate in Int32 and silently wrap, returning wrong totals

`LiteDB/Document/Expression/Methods/Aggregate.cs:106` · overflow · rank #29

**Mechanism.** SUM (and AVG) seed the accumulator with `new BsonValue(0)`, which is BsonType.Int32. `BsonValue.operator+` (LiteDB/Document/BsonValue.cs:468) has a fast path `if (left.IsInt32 && right.IsInt32) return left.AsInt32 + right.AsInt32;`. C# integer arithmetic is unchecked (the project does not set CheckForOverflowUnderflow in LiteDB/LiteDB.csproj), so for a sequence of Int32 values the accumulator never widens and every partial sum wraps modulo 2^32. The invariant an aggregate must maintain — the returned value equals the mathematical sum — is broken with no error, and AVG then divides the wrapped sum by the count.

**Failure.** Collection `files` with three documents whose `size` field is the Int32 2000000000. `SELECT SUM($.size) FROM files` computes 0+2000000000 = 2000000000 (Int32), +2000000000 wraps to -294967296, +2000000000 = 1705032704. The query returns 1705032704 instead of 6000000000, as a normal successful result. `SELECT AVG($.size) FROM files` likewise returns 568344234.67 instead of 2e9.

**Fix.** Seed the accumulator in the widest type implied by the sequence (e.g. accumulate Int32/Int64 in a checked Int64 or Decimal and only narrow at the end), or use checked arithmetic and surface an explicit overflow error.

### H20. 6-bit BsonType mask in index-key header cannot represent BsonType.Vector (100); vector index keys are written but can never be read back

`LiteDB/Utils/ExtendedLengthHelper.cs:21` · on-disk-format · rank #31

**Mechanism.** ReadLength() reserves the top 2 bits of the type byte for the extended string/binary length, so it recovers the BsonType with `typeByte & 0b0011_1111`. That silently assumes every BsonType value fits in 6 bits. BsonType.Vector = 100 (LiteDB/Document/BsonType.cs) does not: WriteIndexKey writes the raw type byte 100 (LiteDB/Utils/Extensions/BufferSliceExtensions.cs:348) and ReadLength decodes 100 & 0x3F = 36, which is not a defined BsonType, so ReadIndexKey falls into `default: throw new NotImplementedException()` (BufferSliceExtensions.cs:198). The `case BsonType.Vector: return buffer.ReadVector(offset);` arm at BufferSliceExtensions.cs:196 is therefore dead code. Nothing rejects a vector as a skip-list index key: BsonExpression.GetIndexKeys does no type filtering, IndexService.AddNode only rejects Min/MaxValue and keys longer than MAX_INDEX_KEY_LENGTH, and BsonValue.GetBytesCount/IndexNode.GetKeyLength size a Vector key as 3 + 4*dim, so any vector with <= 255 dimensions is accepted and persisted.

**Failure.** var col = db.GetCollection("docs"); col.EnsureIndex("emb", "$.Embedding"); (a plain index, not a VectorIndexOptions index) then col.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new float[]{1,2,3}) }); -> succeeds and writes an index node whose key type byte is 100. The next operation that materializes that node throws NotImplementedException: inserting a second document (IndexService.AddNode scans the level-0 list and calls GetNode -> new IndexNode(page,index,segment) -> segment.ReadIndexKey), any FindAll/Find that uses the index, Delete/Update (DeleteAll/DeleteList -> GetNode), and even DropIndex (IndexService.DropIndex:308-315 -> GetNode). The bad node is already durable on disk, so reopening the file and touching the collection keeps throwing; the index cannot be dropped and the documents cannot be deleted, leaving the collection permanently unusable short of a full rebuild.

**Fix.** Reject non-indexable types (Vector/Document/Array-with-vector) as skip-list index keys in IndexService.AddNode, and/or stop stealing type bits: store the extended length in a dedicated byte so the full BsonType byte round-trips. At minimum ReadLength must validate that `typeByte & 0b0011_1111` is a defined BsonType and that the 2 stolen bits are only used for String/Binary.

### H21. Vector page free-list slot uses MAX_INDEX_LENGTH (1400), so GetFreeVectorPage hands back a page too small for the node and the insert throws "InvalidDatafileState"

`LiteDB/Engine/Pages/VectorIndexPage.cs:52` · correctness · rank #33

**Mechanism.** VectorIndexPage copies IndexPage's two-slot free-list scheme, whose single threshold MAX_INDEX_LENGTH = 1400 (LiteDB/Utils/Constants.cs:60) is only sound because a b-tree index node is hard-capped at 1400 bytes. A vector index node is not: VectorIndexNode.GetLength (LiteDB/Engine/Structures/VectorIndexNode.cs:113) returns 172 + 4*dimensions and stores inline for any dimensions <= 1996 (up to 8156 bytes). VectorIndexService.Insert (LiteDB/Engine/Services/VectorIndexService.cs:106) then calls Snapshot.GetFreeVectorPage(length, ref freeList), which — unlike GetFreeDataPage, which picks a graded slot via DataPage.GetMinimumIndexSlot — blindly takes the single free-list head and asserts ENSURE(page.FreeBytes > bytesLength, "this page shout be space enouth for this new vector node") (LiteDB/Engine/Services/SnapShot.cs:361). ENSURE is NOT [Conditional("DEBUG")] (LiteDB/Utils/Constants.cs:137) — it throws LiteException.InvalidDatafileState in release builds. Because AddOrRemoveFreeVectorList keeps a page on the free list while FreeBytes >= 1400, any node whose slot cost (length+4) exceeds 1400 can find the head page holding between 1400 and length+3 free bytes, and the insert aborts. (Even if the first ENSURE were passed, BasePage.InternalInsert re-checks FreeBytes >= bytesLength + SLOT_SIZE and throws LiteException.InvalidFreeSpacePage.)

**Failure.** EnsureIndex with new VectorIndexOptions(768) (a standard embedding size) and insert documents one at a time. Node length L = 172 + 4*768 = 3244, slot cost 3248. A fresh VectorIndexPage has FreeBytes = 8192-32 = 8160. Doc 1: FreeBytes -> 8160-3248 = 4912, 4912 >= 1400 so FreeListSlot = 0 and the page stays as the free-list head. Doc 2: FreeBytes -> 1664, still >= 1400, still the head. Doc 3: GetFreeVectorPage returns that page and ENSURE(1664 > 3244) fails -> LiteException "InvalidDatafileState: this page shout be space enouth for this new vector node", the transaction rolls back and the third document cannot be inserted at all. Enumerating every inline dimension 1..1996 shows 902 of them are affected, including 512 (fails on the 4th insert), 640, 768 (3rd), 1024 (2nd), 1152 and 1536 (2nd). The existing tests only use 2, 6, 8 and 32 dimensions plus one > 1996 (external) case, so the whole 365..1646 window is untested.

**Fix.** Give vector index pages a graded free list (as DataPage has) keyed on the actual node length, or clamp inline storage to nodes <= MAX_INDEX_LENGTH and spill everything larger to external data blocks. At minimum, GetFreeVectorPage must walk the list (or fall back to NewPage) when the head page cannot fit bytesLength + SLOT_SIZE instead of asserting.

### H22. Insert drops the new node's forward links when the reverse prune rejects it, permanently orphaning the node (document silently missing from every vector search) ⚠️ needs more evidence

`LiteDB/Engine/Services/VectorIndexService.cs:197` · correctness · rank #36

**Mechanism.** EnsureBidirectional (line 331) adds the new node to the neighbour's list, re-prunes that list to VectorIndexNode.MaxNeighborsPerLevel = 8 by distance from the neighbour, and returns whether the new node survived. When it did not, Insert also deletes the new node's *forward* edge to that neighbour. If every selected candidate rejects the newcomer, the new node ends the level-0 pass with an empty neighbour list and no inbound edge, i.e. it is not part of the graph at all. Nothing repairs this: Search (VectorIndexService.Search.cs:30) only ever enters at metadata.Root and walks GetNeighbors, TryFindNode (line 488) also BFSes from metadata.Root, and ClearTree (line 539) walks from metadata.Root too. The same edge-cutting happens to *existing* nodes via RemoveBackLink at line 349, which removes the reverse direction as well, so a node whose only surviving neighbour prunes it away is cut loose too.

**Failure.** Cosine or Euclidean index, MaxNeighborsPerLevel = 8. Insert 9 tightly clustered vectors (e.g. random points inside a 0.01 ball); each node's level-0 list saturates with the other 8, all at distance ~0.01. Now insert an outlier, e.g. [1000,1000,...]. SearchLayer returns the 8 cluster nodes; for each, PruneNeighbors scores 9 candidates (8 existing at ~0.01 plus the outlier at ~1414), keeps the 8 closest and discards the outlier, so EnsureBidirectional returns false and line 202 removes the outlier's forward link too. The outlier node exists on disk but is unreachable: (a) a query for a target near [1000,1000,...] never returns that document even though it is the exact nearest neighbour — a wrong result reported as correct; (b) deleting or updating that document is a silent no-op because TryFindNode cannot reach it, leaving a node whose DataBlock points at storage that will be recycled by a later document; (c) Drop/DropCollection (line 68, ClearTree) never frees its page, and Drop then sets metadata.Reserved = uint.MaxValue (line 74), orphaning the page permanently. Worst case: if the new node also sampled a higher level than the current root, line 162 makes it metadata.Root *before* the connection pass, so the orphan becomes the entry point and every previously indexed document disappears from vector search — Search's GreedySearch/SearchLayer find only the root itself.

**Fix.** Keep the new node's forward links regardless of the neighbour's prune outcome (standard HNSW prunes only the neighbour's own list), and stop removing the reverse direction in RemoveBackLink. Additionally guarantee at least one surviving edge (force-insert into the closest neighbour's list, evicting its worst) so no node can end an insert with zero level-0 neighbours.

### H23. DiskReader.ReadStream discards Stream.Read's return value, so a short read silently serves another page's bytes ✅

`LiteDB/Engine/Disk/DiskReader.cs:81` · correctness · rank #37

**Mechanism.** ReadStream is the load factory handed to MemoryCache.GetReadablePage / GetWritablePage. For the readable path, MemoryCache does NOT clear the frame before invoking the factory (MemoryCache.cs:102-112 acquires a recycled frame, transitions it to Loading, and calls factory(position, page) with no Clear()), and the 0xFF scrub of freed frames in TransitionToFreeLocked is `#if DEBUG || TESTING` only (MemoryCache.cs:356-361). So in a Release build a recycled frame still holds the full 8192 bytes of whatever page last occupied it. Because the return value of Stream.Read is ignored, any read that returns fewer than buffer.Count bytes (count==0 included, i.e. position at/after EOF) leaves the tail — or the entire page — as the previous occupant's bytes, and the frame is then published as FrameState.Readable for position `position` and handed to BasePage.ReadPage as if it were valid page content. There is no checksum, so the engine accepts it. The only guard that exists (DEBUG(buffer.All(0)==false)) is compiled out in Release, and the sibling read sites do check: ReadFull asserts the byte count (DiskService.cs:324) and MarkAsInvalidState / PromoteVectorFormat loop until PAGE_SIZE bytes are read (DiskService.cs:279-284, DiskService.FileVersion.cs:487-493) — the same file therefore treats a short read as an error everywhere except here. For an encrypted database the damage is persistent, not per-read: AesStream.Read delegates to a single long-lived CryptoStream (AesStream.cs:178); once that CryptoStream's underlying read returns 0 it transforms the final block and sets _finalBlockTransformed, after which it returns 0 forever, and CryptoStream cannot be seek-reset. That poisoned AesStream is then handed back to the shared pool by DiskReader.Dispose -> StreamPool.Return (DiskReader.cs:102-107) and reused by later readers, so every subsequent page load through it returns 0 bytes and is silently answered with stale frame content.

**Failure.** Open an encrypted file database. A page pointer that resolves past the current end of the log or data file (a log/data file truncated by a crash or external tool, or a header whose LastPageID survived while later pages did not) causes DiskReader.ReadStream to issue one read past EOF: AesStream/CryptoStream returns 0 bytes, the ignored count leaves the recycled frame holding, say, the bytes of collection page #7 that previously occupied that frame, and the cache publishes it as the page at the requested position. The query returns documents from the wrong page as if correct (or throws a nonsense 'invalid page type'), and because the pooled AesStream is now permanently at _finalBlockTransformed=true and is returned to the StreamPool, every later page read that rents it returns 0 bytes and is answered with whatever stale bytes the recycled frame held — turning one out-of-range read into database-wide garbage reads until the process restarts. The same silent-stale-page outcome occurs without encryption for any caller-supplied Stream (public `new LiteDatabase(stream)`) whose Read legally returns fewer bytes than requested: ConcurrentStream.Read (ConcurrentStream.cs:73-83) forwards a single underlying Read with no loop, so a stream that returns 4096 of 8192 bytes yields a page whose second half is another page's data.

**Fix.** Loop until buffer.Count bytes are read and throw if the stream reaches EOF early (same shape as MarkAsInvalidState's loop), e.g. `var offset = 0; while (offset < buffer.Count) { var read = stream.Read(buffer.Array, buffer.Offset + offset, buffer.Count - offset); if (read == 0) throw new EndOfStreamException(...); offset += read; }`. Independently, have MemoryCache.GetReadablePage Clear() the frame before calling the factory (GetWritablePage already does, MemoryCache.cs:190) so a future short read cannot expose another page's bytes.

### H24. BufferReader.ReadIndexKey cannot decode extended-length (>=256 byte) string/binary index keys written by BufferSlice.WriteIndexKey

`LiteDB/Engine/Disk/Serializer/BufferReader.cs:368` · correctness · rank #38

**Mechanism.** The sort/ORDER BY path is asymmetric. The WRITE side (`SortContainer.Insert` -> `BufferSliceExtensions.WriteIndexKey`, BufferSliceExtensions.cs:330) encodes string/binary keys with `ExtendedLengthHelper.WriteLength`, which steals the top 2 bits of the *type* byte to carry bits 8-9 of the length (supporting keys up to MAX_INDEX_KEY_LENGTH = 1023). The READ side used for the same bytes is `BufferReader.ReadIndexKey` (called from `SortContainer.MoveNext`, SortContainer.cs:113), which reads the type byte raw (`(BsonType)this.ReadByte()`, line 356) with no mask, and reads the length as a single byte. For a key whose UTF-8 byte length is <= 255 the extension bits are zero and it works by accident; for 256..1023 bytes the type byte becomes 0x46/0x86/0xC6 (0x40|BsonType.String=6), which matches no `BsonType` member, so control reaches `default: throw new NotImplementedException();` at line 386. `BufferSliceExtensions.ReadIndexKey` (the index-node reader) does mask correctly via `ExtendedLengthHelper.ReadLength`, which is why the bug is invisible on the index path and only bites the sorter. Even if the type matched, the length would be truncated to `length & 0xFF` (300 -> 44), yielding a wrong string.

**Failure.** using var db = new LiteDatabase(":memory:"); var col = db.GetCollection("c"); col.Insert(new BsonDocument { ["_id"] = 1, ["txt"] = new string('a', 300) }); col.Query().OrderBy("$.txt").ToList();  -- `txt` has no index, so QueryOptimization routes through BasePipe.OrderBy -> SortService.Insert -> SortContainer.Insert, which writes the key as typeByte=0x46 (=0x40|6), lengthByte=44. Because there is a single container, SortService calls container.InitializeReader(null, _buffer, ...) -> MoveNext() -> BufferReader.ReadIndexKey(), which sees (BsonType)70 and throws System.NotImplementedException out of the user's ToList() call. Same for GROUP BY on such a field, and for byte[] values >= 256 bytes (typeByte 0x40|9). Note multi-segment ORDER BY escapes the bug because SortKey is serialized as a BsonArray (4-byte length prefixes), so only single-segment ORDER BY/GROUP BY crashes. IndexNode.GetKeyLength(300-byte string) = 302 <= 1023, so no LiteException.InvalidIndexKey guard fires first, and LiteDB.Tests/Internals/Sort_Tests.cs only ever sorts 36-char GUID strings, so no test covers this.

**Fix.** Decode with the same codec used to encode: read two bytes and call `ExtendedLengthHelper.ReadLength(typeByte, lengthByte, out var type, out var len)`, then use `len` for the String/Binary cases (and skip the second byte for the fixed-size types).

### H25. IndexLookup.Load(PageAddress) treats a DataBlock address as an index-node address, breaking every index-key-only plan that sorts or replays documents

`LiteDB/Engine/Query/Lookup/IndexKeyLoader.cs:34` · api-contract · rank #40

**Mechanism.** The `IDocumentLookup` contract is round-trip: `Load(IndexNode)` stamps `doc.RawId`, and the pipeline later re-loads the same document by handing that RawId back to `Load(PageAddress)` (BasePipe.cs:179 and :213 after the merge sort, and DocumentCacheEnumerable.cs:67/:96 on cache replay). `DatafileLookup` honours it (stamps `node.DataBlock`, reads `_data.Read(dataBlock)`). `IndexLookup` violates it: it stamps `node.DataBlock` (a DataPage address) but resolves the same value through `IndexService.GetNode(address)`, which does `_snapshot.GetPage<IndexPage>(address.PageID)` (LiteDB/Engine/Services/IndexService.cs:197). Constructing an `IndexPage` over a DataPage buffer throws `LiteException.InvalidPageType(PageType.Index, this)` (LiteDB/Engine/Pages/IndexPage.cs:21); if the data page happens to already sit in the snapshot's `_localPages`, the `(T)page` cast in Snapshot.GetPage throws InvalidCastException instead. `IndexLookup` is selected whenever `QueryPlan.IsIndexKeyOnly` is true (QueryPlan.cs:134), and nothing in QueryOptimization prevents IsIndexKeyOnly from coexisting with a sort-service ORDER BY: IsIndexKeyOnly is set purely from `Fields.Count == 1 && IndexExpression == "$." + field` (QueryOptimization.cs:222-226), while DefineOrderBy only drops the OrderBy when the primary segment's *source text* equals the index expression and there is exactly one segment (QueryOptimization.cs:345-353).

**Failure.** `col.EnsureIndex("name"); db.Execute("SELECT $.name FROM col ORDER BY LOWER($.name)")`. Fields = {name}; ChooseIndex finds no WHERE term, falls through to the `preferred` branch (QueryOptimization.cs:295-306) and picks the `name` index, so IndexExpression = "$.name" and IsIndexKeyOnly = true. DefineOrderBy keeps the OrderBy because "LOWER($.name)" != "$.name". BasePipe.OrderBy inserts (key, doc.RawId=DataBlock) pairs into SortService, then calls `_lookup.Load(keyValue.Value)` -> `IndexLookup.Load(PageAddress)` -> `GetNode(dataBlock)` -> `GetPage<IndexPage>(dataPageId)` and the query dies with `LiteException: Invalid page type` (or InvalidCastException). Same crash from `SELECT $.name FROM col GROUP BY UPPER($.name)` (GroupByPipe.cs:42), from a two-segment ORDER BY on the indexed field (`ORDER BY $.name, LENGTH($.name)` — Segments.Count != 1 so the OrderBy survives), and from any aggregate/GROUP BY select that enumerates a DocumentCacheEnumerable twice (e.g. `{ n: COUNT($.name), s: SUM($.name) }`), whose second pass replays through Load(PageAddress).

**Fix.** Either make IndexLookup store the index-node `Position` as the replay handle (keeping DataBlock only for de-duplication), or add a distinct replay key to IDocumentLookup so Load(PageAddress) is always given the address its own Load(IndexNode) produced. A stop-gap is to clear `IsIndexKeyOnly` in QueryOptimization whenever `_queryPlan.OrderBy != null || _queryPlan.GroupBy?.OrderBy != null`.

### H26. Cross-type numeric CompareTo throws OverflowException for Doubles outside decimal range or NaN

`LiteDB/Document/BsonValue.cs:563` · error-handling · rank #41

**Mechanism.** When the two BsonValues have different BsonTypes but both are numeric, comparison funnels through `Convert.ToDecimal(object)`. `Convert.ToDecimal(double)` throws OverflowException when |value| > Decimal.MaxValue (~7.9e28) and when the value is NaN or +/-Infinity. Nothing in the write or read path rejects such doubles: BufferWriter.WriteElement writes the raw 8-byte IEEE value (BufferWriter.cs:~352) and the reader returns it verbatim; a grep for IsNaN/IsInfinity shows guards only in the vector code and JsonWriter, never on index keys or comparisons. So a single out-of-range Double stored in an indexed or filtered field makes every comparison against a differently-typed number throw.

**Failure.** Insert `{_id:1, a: 1e300}` (Double) into collection `c`, `EnsureIndex("a")`, then insert `{_id:2, a: 5}` (Int32). IndexService.AddNode scans from head and evaluates `rightNode.Key.CompareTo(key, _collation)` where rightNode.Key is Double(1e300) and key is Int32(5): types differ, both IsNumber, so `Convert.ToDecimal(1e300)` throws OverflowException, which escapes AddNode -> Insert -> the transaction as a raw System.OverflowException (not a LiteException), aborting the insert. The same throw happens with no index at all for `SELECT $ FROM c WHERE a > 0` (BsonExpressionOperators.GT -> `operator >` -> CompareTo) and for a mapped POCO containing `double.NaN`. Result: an unhandled OverflowException on data the engine happily accepted and persisted.

**Fix.** Special-case non-finite and out-of-decimal-range Doubles before converting: compare as Double when either side is Double (ordering NaN consistently, as Double.CompareTo already does), or clamp/route through a comparison that cannot overflow, instead of unconditionally calling Convert.ToDecimal.

### H27. CreateSnapshot disposes a read snapshot that an open cursor is still iterating, turning a common transaction pattern into an engine shutdown that stamps the data file as invalid

`LiteDB/Engine/Services/TransactionService.cs:91` · correctness · rank #42

**Mechanism.** CreateSnapshot upgrades a cached Read snapshot to Write by calling snapshot.Dispose() (line 91) and replacing the map entry (line 97). Nothing checks transaction.OpenCursors - only LiteEngine.BeginTrans (Transaction.cs:23) and LiteEngine.Commit (Transaction.cs:42) do that, and AutoTransaction (used by Insert/Update/Delete/EnsureIndex) does not. QueryExecutor.ExecuteQuery captures the snapshot instance in its lazy iterator (QueryExecutor.cs:91) and its IndexService/DataService keep calling snapshot.GetPage on every MoveNext. Snapshot.Dispose sets `_disposed = true` and Clear() releases all local page buffers plus the collection page, so the next GetPage hits `ENSURE(!_disposed, "the snapshot is disposed")` (SnapShot.cs:198). ENSURE is NOT conditional (Constants.cs:137) - it throws LiteException.InvalidDatafileState in release builds. QueryExecutor.cs:157-160 then routes it through EngineState.Handle, which for ErrorCode == INVALID_DATAFILE_STATE calls _engine.Close(ex); LiteEngine.Close(ex) sees TryCatch.InvalidDatafileState and calls _disk.MarkAsInvalidState(), writing byte 1 at HeaderPage.P_INVALID_DATAFILE_STATE into the data file header on disk.

**Failure.** db.BeginTrans(); foreach (var d in col.Find(x => x.Active)) { d.Seen = true; col.Update(d); } - Find is lazy (LiteQueryable.ToEnumerable), so the first MoveNext creates a Read snapshot on "col" inside the explicit transaction. col.Update goes through AutoTransaction -> the same transaction -> CreateSnapshot(LockMode.Write, "col", false) -> first branch is true (mode Write, existing Read) -> the in-use Read snapshot is disposed and its page buffers returned to the cache. The second MoveNext throws LiteException "the snapshot is disposed" (INVALID_DATAFILE_STATE); the whole LiteDatabase instance is closed by EngineState.Handle, the uncommitted transaction is discarded by TransactionMonitor.Dispose, and the healthy data file is permanently flagged invalid (with auto-rebuild=true the next open runs a full Recovery/rebuild).

**Fix.** Refuse the upgrade (or defer it) while transaction.OpenCursors is non-empty - throw the same explicit "close cursors first" LiteException that BeginTrans/Commit use - instead of disposing a snapshot other iterators still reference; at minimum do not surface it as INVALID_DATAFILE_STATE, which self-closes the engine and marks the file for rebuild.

### H28. If TransactionService.Commit() or Rollback() throws, the transaction is never released: collection write lock and engine read lock stay held for the life of the engine

`LiteDB/Engine/Engine/Transaction.cs:110` · error-handling · rank #43

**Mechanism.** CommitAndReleaseTransaction (Transaction.cs:108-120), LiteEngine.Rollback (58-74) and AutoTransaction's catch block (95-105) all call `transaction.Commit()`/`transaction.Rollback()` and then `_monitor.ReleaseTransaction(transaction)` as two sequential statements with no try/finally. TransactionService.Commit() disposes its snapshots only after PersistDirtyPages returns (line 266 -> 277), and Rollback() calls ReturnNewPages() (line 298) before disposing snapshots, so both can throw with every snapshot still live. Each write-mode Snapshot holds a CollectionLock acquired with Monitor.TryEnter in its constructor, released only in Snapshot.Dispose(). When ReleaseTransaction is skipped: the TransactionService stays in TransactionRegistry, ThreadLocal `_slot` still points at it, _locker.ExitTransaction() is never called (read lock stays held) and every write-mode snapshot keeps its per-collection Monitor. Note LiteEngine.Commit()/Rollback() have no catch at all, so EngineState.Handle is never invoked and the engine is not even closed - the process keeps running with a wedged lock.

**Failure.** Thread A: db.BeginTrans(); col.Insert(many docs) (a write snapshot on "orders" now holds that collection's lock); db.Commit(). The log append inside TransactionService.Commit -> PersistDirtyPages -> DiskService.WriteLogDisk throws IOException (disk full / network share error). `_monitor.ReleaseTransaction` is skipped. From then on: thread B's `col.Insert` on "orders" blocks in LockService.EnterLock for the whole Pragmas.Timeout and then throws LockTimeout - forever; TryCheckpoint/Checkpoint can never get the exclusive lock because thread A's read lock is still counted; thread A's own `_slot` still holds the dead transaction, so its next AutoTransaction reuses it and hits ENSURE(_state == Active) in CreateSnapshot, which raises LiteException INVALID_DATAFILE_STATE and makes EngineState.Handle close the engine and stamp P_INVALID_DATAFILE_STATE into the data file header.

**Fix.** Wrap Commit()/Rollback() in try/finally (or try/catch that still calls ReleaseTransaction) in CommitAndReleaseTransaction, LiteEngine.Rollback and AutoTransaction's catch, so the monitor entry, thread slot, collection locks and the transaction read lock are always released even when the commit/rollback path fails.

### H29. Releasing a query transaction from a different thread silently fails to exit the transaction read lock, permanently disabling checkpoints and poisoning the original thread with LockRecursionException

`LiteDB/Engine/Services/TransactionMonitor.cs:125` · concurrency · rank #44

**Mechanism.** The engine-wide transaction lock is a ReaderWriterLockSlim whose read entry is thread-affine, but the release path is not tied to the creating thread. QueryExecutor.cs:83 registers `_monitor.ReleaseTransaction(transaction)` as an OnDispose callback on the lazily-enumerated result, so it runs on whichever thread disposes the enumerator. ReleaseTransaction removes the entry, then calls _locker.ExitTransaction() (TransactionMonitor.cs:125-128); LockService.ExitTransaction only acts `if (_transaction.IsReadLockHeld)` (LockService.cs:58), which is false on the disposing thread, so the read entry taken by the creating thread is leaked with no error. Two consequences follow. (a) LockService.TryEnterExclusive bails out whenever `_transaction.CurrentReadCount > 0` (LockService.cs:120), so WalIndexService.TryCheckpoint returns 0 forever and the log file grows without bound; EnterExclusive (explicit Checkpoint/Rebuild) waits on readers that will never exit and throws LockTimeout. (b) The creating thread now holds the read lock but has no registry entry, so its next GetTransaction computes `alreadyLock == false` (TransactionMonitor.cs:60) and calls EnterTransaction -> TryEnterReadLock on a ReaderWriterLockSlim built with LockRecursionPolicy.NoRecursion (LockService.cs:20) -> LockRecursionException, not a LiteException, on every subsequent database call from that thread.

**Failure.** ASP.NET handler: `foreach (var doc in col.Find(pred)) { await ProcessAsync(doc); }`. ExecuteQuery runs on thread-pool thread A and takes the read lock there; after the first await the continuation resumes on thread B, so the compiler-generated finally disposes the enumerator on B. ReleaseTransaction on B removes the transaction and calls ExitTransaction, which no-ops because B holds no read lock. Thread A's read entry is leaked for the lifetime of the engine: db.Checkpoint() now throws LockTimeout("exclusive"), the automatic post-commit TryCheckpoint silently never fires so the .log file grows until the disk fills, and the next query served on thread A throws LockRecursionException("Write/read lock ... recursive") instead of working.

**Fix.** Record on the TransactionService whether it actually entered the transaction lock and on which thread, and release the read entry on that thread (or use a non-thread-affine counting primitive such as SemaphoreSlim/an explicit reader counter) instead of relying on ReaderWriterLockSlim.IsReadLockHeld on whatever thread happens to dispose the reader. At minimum, make ExitTransaction fail loudly instead of silently skipping.

### H30. LiteDatabase.Dispose throws and skips engine disposal when an explicit transaction is still open

`LiteDB/Client/Database/LiteDatabase.cs:400` · error-handling · rank #45

**Mechanism.** The Stream constructor (lines 71-90) stores `_checkpointOverride` whenever the header's CHECKPOINT pragma differs from 1, and `Dispose(bool)` restores it *before* calling `_engine.Dispose()`. `LiteEngine.Pragma(string, BsonValue)` (LiteDB/Engine/Engine/Pragma.cs:22-26) is a *write* operation: after seeing the value differs it executes `if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();`. `LockService.IsInTransaction` (LiteDB/Engine/Services/LockService.cs:31) is `_transaction.IsReadLockHeld || IsWriteLockHeld`, and `ReaderWriterLockSlim.IsReadLockHeld` is per-thread, so it is true on exactly the thread that called `BeginTrans()` without Commit/Rollback. The exception propagates out of `Dispose(bool)` and `_engine.Dispose()` on line 403 is never reached, so `LiteEngine.Close()` never runs: TransactionMonitor, DiskService (and its writer queue/streams), SortDisk temp file and LockService are all left alive, and the shutdown checkpoint that is the only way the volatile in-memory WAL (EngineSettings.CreateLogFactory falls through to `new StreamFactory(new MemoryStream(), ...)` when LogStream is null) reaches the caller's data stream is skipped. Introduced by commit bf3987f ("Ensure external streams checkpoint on commit"); before it, Dispose was just `_engine.Dispose()`.

**Failure.** ```
using var stream = new FileStream("data.db", FileMode.OpenOrCreate);
using var db = new LiteDatabase(stream);            // non-MemoryStream, writable -> _checkpointOverride = 1000
db.BeginTrans();
db.GetCollection<Order>().Insert(order);
throw new InvalidOperationException("business rule failed");   // or simply forget Commit()
```
At the end of the `using`, `Dispose()` calls `_engine.Pragma(CHECKPOINT, 1000)`, `_locker.IsInTransaction` is true on this thread, and a `LiteException` ("The current thread already contains an open transaction...") is thrown *from Dispose*. It replaces the caller's original exception, and `_engine.Dispose()` never runs, so the disk service, its background writer, the sort temp file and the lock service leak for the lifetime of the process, the data file's exclusive handle is never released, and any commit whose eager `TryCheckpoint()` was skipped (WalIndexService.TryCheckpoint returns 0 whenever another thread has a read lock) is silently discarded with the in-memory log.

**Fix.** Wrap the pragma restore in try/catch (or check for an open transaction first) and always call `_engine.Dispose()` in a `finally`; better still, do not perform a write transaction during Dispose at all — pass the desired shutdown checkpoint behaviour to the engine instead of mutating a persisted pragma.

### H31. LiteFileStream overwrite deletes all existing chunks non-atomically; an interrupted upload yields silent truncation on read and a permanently un-writable file id

`LiteDB/Client/Storage/LiteFileStream.cs:47` · durability · rank #46

**Mechanism.** The write-mode constructor deletes every stored chunk of the file immediately, but only mutates the in-memory LiteFileInfo (`_file.Length = 0; _file.Chunks = 0`). The persisted `_files` document keeps the old Length/Chunks until `WriteChunks(flush:true)` upserts it, which happens only in Flush/Dispose. Chunk inserts and the file upsert are separate implicit transactions, so nothing is crash-atomic. In addition, `ENSURE(count == _file.Chunks)` at line 49 is NOT debug-only - Constants.ENSURE (LiteDB/Utils/Constants.cs:136) has no [Conditional] attribute and throws LiteException.InvalidDatafileState in Release - and it runs AFTER the DeleteMany has already committed.

**Failure.** File id "F" is stored with Length=600000, Chunks=3. `storage.Upload("F", "f.bin", src)` is interrupted by a process kill (or the caller drops the LiteFileStream without disposing - Stream has no finalizer, so Flush never runs) after two chunks (0:261120 + 1:66560 = 327680 bytes) were inserted. Persisted state: files doc still Length=600000/Chunks=3, chunks 0..1 only. Consequences, all with no error surfaced: (a) `storage.Download("F", dest)` returns the LiteFileInfo (Length=600000) but writes only 327680 bytes - Read returns 0 once GetChunkData(2) yields null (LiteFileStream.Read.cs:15-45), i.e. silent truncation reported as success; (b) `storage.OpenRead("F").Seek(400000, Begin)` throws NullReferenceException at LiteFileStream.Read.cs:86 (`seekStreamPosition += _currentChunkData.Length` with _currentChunkData == null for the missing chunk 2); (c) every retry of `Upload("F", ...)` deletes the two surviving chunks and then throws LiteException 'invalid datafile state' at line 49 (count=2 != Chunks=3), and every later retry throws again (count=0 != 3) - the id can never be written through the API again without manual surgery on the chunks collection.

**Fix.** Wrap the delete+insert+upsert sequence in an explicit transaction, persist the reset Length/Chunks before deleting chunks (or write new chunks under a new generation key and delete the old ones after the metadata commit), and replace the ENSURE with tolerant cleanup instead of a hard InvalidDatafileState throw.

### H32. Deserialize returns RawValue unconverted for the 10 native BSON types, so any stored/declared numeric-width mismatch throws InvalidCastException from the compiled setter

`LiteDB/Client/Mapper/BsonMapper.Deserialize.cs:141` · error-handling · rank #48

**Mechanism.** For the types in `_bsonTypes` (String, Int32, Int64, Boolean, Guid, DateTime, Byte[], ObjectId, Double, Decimal) Deserialize returns `value.RawValue` with no conversion, while the very next branch converts `_basicTypes` (Int16/UInt32/Single/Char/...) with `Convert.ChangeType`. The value then goes to the setter built by Reflection.CreateGenericSetter, which is `Expression.ConvertChecked(object -> dataType)` (Reflection.Expression.cs:71) — an unbox that requires the boxed runtime type to match the member type exactly. A boxed Int32 cannot be unboxed as Int64/Double/Decimal, so the assignment throws InvalidCastException. This makes the mapper's tolerance of stored BSON types inconsistent: a `float` property accepts an Int32, a `double`/`decimal`/`long` property does not.

**Failure.** Entity `class Product { public int Id {get;set;} public decimal Price {get;set;} }`. A document is written by any of LiteDB's own non-typed paths — `db.Execute("INSERT INTO products VALUES {_id:1, Price:10}")`, LiteDB Studio, or a JSON import via JsonSerializer (JsonReader.cs:89 produces Int32 for `10` and Double for `10.5`). Price is stored as BsonType.Int32/Double. `db.GetCollection<Product>().FindAll()` -> LiteQueryable.ToEnumerable (LiteQueryable.cs:313, no try/catch) -> Deserialize(typeof(decimal), BsonValue(10)) returns a boxed Int32 -> setter throws `InvalidCastException: Unable to cast object of type 'System.Int32' to type 'System.Decimal'`. The document is readable as a BsonDocument but permanently unreadable through the typed API. Same for a `long` property whose field is stored as Int32.

**Fix.** For the numeric members of `_bsonTypes` use the BsonValue accessor matching the target type (AsInt32/AsInt64/AsDouble/AsDecimal) or `Convert.ChangeType(value.RawValue, type, CultureInfo.InvariantCulture)` when RawValue's type differs from `type`, and keep the fast `RawValue` return only when `value.RawValue.GetType() == type`.

### H33. ANY/ALL operator text is emitted without parentheses, so the BsonExpression parser re-associates it and the predicate silently matches nothing

`LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs:453` · correctness · rank #49

**Mechanism.** `Contains`/`Any` are translated to the multi-token operators `# ANY = @0` / `@0 ANY = @1` (ICollectionResolver.cs:19, EnumerableResolver.cs:75) and are written into the builder bare. VisitBinary only parenthesizes an operand when `andOr` is true (i.e. for AndAlso/OrElse, via VisitAsPredicate). For any other binary node the left operand is emitted unwrapped, producing text like `($.Tags ANY = @p0 = @p1)`. In BsonExpressionParser the operator table (`_operators`, BsonExpressionParser.cs:29-82) is walked in insertion order, and plain `=` appears *before* `ANY =`, so `=` binds tighter: `ops.IndexOf("=")` is found first and the parser builds `$.Tags ANY = (@p0 = @p1)`. The ANY comparison is then performed against the value of the *comparison* rather than against the intended item. Note `!x.Tags.Contains(..)` works only by accident, because VisitUnary happens to wrap the operand in parentheses (line 302-305).

**Failure.** `col.Find(x => x.PhoneNumbers.Contains(1234) == false)` generates `($.PhoneNumbers ANY = @p0 = @p1)` with p0=1234, p1=false. The parser evaluates it as `$.PhoneNumbers ANY = (1234 = false)` = `$.PhoneNumbers ANY = false`, which is true only for documents whose array literally contains the boolean `false`. The query returns 0 documents instead of every user who does not have phone 1234. Same for `x => x.Active == x.Tags.Contains("a")`, which parses as `($.Active = $.Tags) ANY = @p0`.

**Fix.** Always wrap each operand of a generated binary operator in parentheses (or, at minimum, wrap any sub-expression whose emitted text ends in an ANY/ALL comparison), instead of only when `andOr` is true.

### H34. Dictionary/indexer string key is interpolated into the expression text without escaping, allowing expression injection

`LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs:199` · security · rank #50

**Mechanism.** For `get_Item(string)` calls the index is evaluated to a CLR string and then concatenated straight into the generated expression source inside single quotes. Every other value the visitor handles goes through `_parameters` (VisitConstant, lines 269-280), but this one does not. The Tokenizer terminates a single-quoted string at the first unescaped `'` (Utils/Tokenizer.cs:624-631), so a key containing an apostrophe closes the literal and the rest of the key is parsed as expression syntax.

**Failure.** An application filters on a user-supplied metadata key: `col.Find(x => x.MetaData[key] == value)`. With key = `a'] = 1 OR $.IsAdmin = true OR $.z['b`, the generated source is `($.MetaData.['a'] = 1 OR $.IsAdmin = true OR $.z['b'] = @p0)`, which parses successfully and returns every admin document regardless of the intended filter — a filter bypass driven purely by input data. Even a benign key such as `O'Brien` breaks the generated text and surfaces as NotSupportedException ("Invalid BsonExpression when converted from Linq expression").

**Fix.** Emit the key as a parameter (e.g. append `[@pN]`-style parameterized member access, or at minimum escape `\` and `'` in the key the way the Tokenizer expects) instead of interpolating the raw string.

### H35. Enum name substitution fires for every binary operator and compiles the right operand even when it references the lambda parameter

`LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs:457` · correctness · rank #51

**Mechanism.** The guard only checks that the *left* operand is `Convert(enum, Int32)`; it does not check the node type (it applies to `+`, `-`, `>`, `<=`, ... as well as `=`/`!=`) and it does not check that the right operand is a constant. `Evaluate(node.Right)` (line 711-740) calls `Expression.Lambda(expr).Compile()` on an expression that may still contain the document's ParameterExpression, which the LINQ compiler rejects. When the right side is constant but the operator is relational, the int is replaced by the enum's *name*, turning an ordinal comparison into a collation string comparison.

**Failure.** (1) `col.Find(x => x.Type == x.PreferredType)` (two enum properties of the same document, EnumAsInteger = false): the left side is `Convert(x.Type, Int32)`, so `Evaluate(node.Right)` compiles `Expression.Lambda(Convert(x.PreferredType, Int32))` and throws InvalidOperationException: "variable 'x' of type 'User' referenced from scope '', but it is not defined". (2) `enum Priority { Low, Medium, High }` with `col.Find(x => x.Priority >= Priority.Medium)` generates `($.Priority >= @p0)` with p0 = "Medium"; the stored string "High" sorts before "Medium", so High-priority documents are silently omitted.

**Fix.** Restrict the branch to `Equal`/`NotEqual` nodes whose right operand is a ConstantExpression (or otherwise parameter-free), and throw NotSupportedException for relational comparisons on string-serialized enums.

### H36. Enum comparison emits a null parameter when Enum.GetName has no name for the value (flags combinations, undefined casts)

`LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs:463` · correctness · rank #52

**Mechanism.** With the default `EnumAsInteger = false`, enums are stored as `obj.ToString()` (BsonMapper.Serialize.cs:117-125). To match that, VisitBinary rewrites the int constant that Roslyn generates for an enum comparison into its name via `Enum.GetName`. `Enum.GetName` returns null for any value that is not a single declared member — which is exactly the case for a combined [Flags] value or a cast of an out-of-range integer. The null is then passed to `Expression.Constant(null)`, VisitConstant maps it to BsonValue.Null (line 276), and the predicate becomes `field = null` with no error anywhere.

**Failure.** `[Flags] enum Perm { Read = 1, Write = 2 }`; a document is stored with Perm.Read|Perm.Write, serialized as the string "Read, Write". `col.Find(x => x.Perm == (Perm.Read | Perm.Write))` produces left `$.Perm` and right `Enum.GetName(typeof(Perm), 3)` = null, i.e. the expression `($.Perm = @p0)` with p0 = null. The matching document is never returned and no exception is raised; documents that are missing the field entirely match instead. `x => x.Status == (Status)99` behaves the same way.

**Fix.** Use the same serialization path as the mapper (`_mapper.Serialize(enumType, Enum.ToObject(enumType, value))`, i.e. effectively `value.ToString()`, which yields "Read, Write") instead of `Enum.GetName`, and throw NotSupportedException if no representation can be produced.

### H37. Member-access stack is applied to every constant visited below it, throwing TargetException for a property access on a filtered collection result

`LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs:257` · error-handling · rank #53

**Mechanism.** VisitMember pushes the MemberExpression on `_memberAccessNodes` before visiting its inner expression (line 152) and pops only after (line 163). VisitConstant assumes any non-empty stack means "this constant is the closure object at the bottom of a member chain" and applies every stacked member to the constant's value. When the inner expression of a member access is a method call whose nested lambda contains an ordinary literal, that literal is visited while the outer member is still on the stack, so `propertyInfo.GetValue(value)` is invoked with an unrelated object. The exception is raised inside `Visit`, outside the try/catch in `Resolve` (lines 72-89), so it is not even wrapped in NotSupportedException.

**Failure.** `col.Find(x => x.Phones.First(p => p.Prefix == 1).Number == 555)`: VisitMember pushes the `Number` PropertyInfo, then Enumerable.First resolves to `FIRST(FILTER(@0 => @1))`, whose `@1` visits the lambda body `p.Prefix == 1`. VisitConstant is then called for the literal `1` with `_memberAccessNodes = [Number]`, executing `Phone.Number.GetValue((object)1)` → System.Reflection.TargetException ("Object does not match target type") escapes to the caller. The same happens for `x => x.Phones.Where(p => p.Prefix == 1).First().Number` and any member access on the result of a nested lambda containing a literal.

**Fix.** Only unwrap the member chain when the ConstantExpression actually is the root of the pushed chain (e.g. record, together with each pushed node, the expression it expects as its inner operand, and skip unwrapping when `node` is not that inner expression), or push onto the stack only while visiting non-parameter (closure) member chains.

### H38. SUBSTRING/INDEXOF do not clamp indices; ArgumentOutOfRangeException aborts the whole query

`LiteDB/Document/Expression/Methods/String.cs:126` · bounds · rank #54

**Mechanism.** SUBSTRING passes `startIndex.AsInt32`/`length.AsInt32` straight to `String.Substring`, and INDEXOF(value, search, startIndex) passes `startIndex.AsInt32` straight to `String.IndexOf`. Neither clamps to [0, s.Length]. Every other type mismatch in this file degrades to BsonValue.Null, so callers reasonably expect null-on-bad-input, but an out-of-range index throws ArgumentOutOfRangeException. The exception propagates out of the compiled LINQ expression, through the query pipeline, and out of `QueryExecutor.ExecuteQuery` (LiteDB/Engine/Query/QueryExecutor.cs:136 only calls `_state.Handle(ex)`, which returns true for non-IO exceptions and rethrows), so one bad document kills the entire resultset — including rows already being streamed.

**Failure.** Collection `users` where most `name` values are long but one document has `name = "abc"`. `SELECT SUBSTRING($.name, 0, 5) FROM users` streams results until it reaches that document, then throws `ArgumentOutOfRangeException: Index and length must refer to a location within the string`, aborting the query mid-enumeration. Same for `SELECT INDEXOF($.name, 'x', 10)`. The repository's own tests document this as broken behaviour (LiteDB.Tests/Expressions/Expressions_Exec_Tests.cs:378 and :393: "expected: false, actual: exception").

**Fix.** Clamp: `var s = value.AsString; var i = Math.Max(0, Math.Min(s.Length, startIndex.AsInt32)); var len = Math.Max(0, Math.Min(s.Length - i, length.AsInt32)); return s.Substring(i, len);` and clamp INDEXOF's startIndex the same way (returning -1 when past the end).

### H39. FIRST/LAST return CLR null on an empty sequence, causing NullReferenceException when nested

`LiteDB/Document/Expression/Methods/Aggregate.cs:62` · null-propagation · rank #55

**Mechanism.** `values.FirstOrDefault()` / `values.LastOrDefault()` return CLR `null` for an empty sequence, not `BsonValue.Null`. The expression compiler nests method results directly: `Expression.Call(method, args.ToArray())` in LiteDB/Document/Expression/Parser/BsonExpressionParser.cs:983, with no null coalescing between a callee's return value and the caller's parameter. Every other method in this namespace dereferences its BsonValue argument immediately (`value.IsString`, `left.IsNull`, `value.Type`), so a nested FIRST/LAST over an empty array dereferences null. The null is only masked when the value lands directly in a document, because `BsonDocument`'s setter coalesces (`this.RawValue[key] = value ?? BsonValue.Null`, LiteDB/Document/BsonDocument.cs:59).

**Failure.** Document `{_id:1, tags:[]}` (or simply no `tags` field). `SELECT COALESCE(FIRST($.tags[*]), 'none') FROM col` → FIRST returns CLR null → `COALESCE` executes `left.IsNull` on null → NullReferenceException escapes the query pipeline. Identically for `UPPER(FIRST($.tags[*]))`, `LENGTH(LAST($.tags[*]))`, `IS_NULL(FIRST($.tags[*]))`.

**Fix.** `return values.FirstOrDefault() ?? BsonValue.Null;` and the same for LAST.

### H40. Unbounded recursion in JsonReader lets untrusted JSON kill the process with an uncatchable StackOverflowException

`LiteDB/Document/Json/JsonReader.cs:76` · security · rank #56

**Mechanism.** ReadValue -> ReadObject -> ReadValue and ReadValue -> ReadArray -> ReadValue form a mutually recursive descent parser with no nesting-depth limit (no MaxDepth exists in JsonReader, JsonSerializer or Tokenizer). Each nesting level costs two stack frames. A .NET StackOverflowException cannot be caught: the CLR terminates the process immediately, bypassing try/catch, finally and using blocks.

**Failure.** A ~20 KB string of 10,000 repeated '[' characters -- supplied via JsonSerializer.Deserialize, via SELECT JSON(@0), via a stored document field evaluated by SELECT JSON($.payload) FROM col, or via a $file_json import -- recurses ~20,000 frames deep on the default 1 MB stack and terminates the host process instead of raising a parse error. LiteDB/Document/Expression/Methods/Misc.cs:29 only catches LiteException with UNEXPECTED_TOKEN, and no catch block can intercept a stack overflow. Any in-flight transaction dies with the process.

**Fix.** Track nesting depth in a field (or pass it through ReadValue/ReadObject/ReadArray) and throw a LiteException once it exceeds a bounded maximum, as System.Text.Json does with MaxDepth. Apply the same bound to JsonWriter.WriteValue, which is also unboundedly recursive.

### H41. $file CSV import silently drops the last row of any file without a trailing newline — including LiteDB's own CSV export

`LiteDB/Engine/SystemCollections/SysFileCsv.cs:240` · correctness · rank #58

**Mechanism.** ReadString sets `newLine = (c == '\n' || c == '\r')`. When the final field ends at EOF, the unquoted loop (line 233) exits with c == -1, so newLine is false; the quoted branch likewise assigns `c = next` == -1 (line 222). The Input loop only emits a document when newLine is true (lines 68-74); the next ReadString call returns null and the loop does `yield break` at line 60, discarding the fully-populated in-progress `doc`. The asymmetry is self-inflicted: Output writes the record separator *before* each subsequent record (`if (index++ == 0) { ...header... } else { writer.WriteLine(); }`, lines 98-122) and never terminates the last line, so every CSV file this engine produces triggers the reader bug. Verified by simulating ReadString/Input: input "a,b\n1,2\n3,4" yields only {a:1,b:2}; "a,b\n1,2\n3,4\n" yields both rows.

**Failure.** `db.Execute("SELECT $ INTO $file('dump.csv') FROM orders")` exports 1000 orders (no trailing newline). Re-importing with `db.Execute("SELECT $ INTO orders_copy FROM $file('dump.csv')")` inserts only 999 documents and reports success — order #1000 is silently lost. Any third-party CSV without a trailing newline loses its last record the same way.

**Fix.** In Input, after the read loop terminates, yield the pending `doc` if index > 0 (i.e. treat EOF as an implicit record terminator); and/or have Output terminate the final line.

### H42. $file output with overwritten:true uses FileMode.OpenOrCreate, leaving the tail of a longer previous file

`LiteDB/Engine/SystemCollections/SysFileJson.cs:58` · correctness · rank #59

**Mechanism.** FileMode.OpenOrCreate opens an existing file positioned at 0 without truncating it. The writer overwrites only the first N bytes; everything beyond N from the previous run survives. Neither Output method sets the length (no FileStream.SetLength, no FileMode.Create/Truncate) and the stream is simply disposed at line 86. SysFileCsv.cs:100 has the identical `new FileStream(filename, overwritten ? FileMode.OpenOrCreate : FileMode.CreateNew)`.

**Failure.** Day 1: `SELECT $ INTO $file('export.json', {overwritten:true}) FROM orders` writes 10,000 documents (say 4 MB). Day 2 the collection has been pruned to 10 documents; the same statement writes ~4 KB and closes the file. export.json is now `[ ...10 docs... ]` immediately followed by the remaining ~4 MB of day-1 text — it fails JSON parsing outright, and the CSV equivalent parses as thousands of bogus extra rows that re-import cleanly into a collection. The command returns 10 and reports no error.

**Fix.** Use FileMode.Create when overwritten is true (or call fs.SetLength(0) right after opening).

### H43. $cols and $indexes lazily enumerate HeaderPage's plain Dictionary while commits mutate it

`LiteDB/Engine/SystemCollections/SysCols.cs:13` · concurrency · rank #60

**Mechanism.** HeaderPage._collections is a `Dictionary<string, BsonValue>` (BsonDocument's backing store, BsonDocument.cs:14-16) and `HeaderPage.GetCollections()` is a lazy iterator over `_collections.GetElements()` -> `RawValue.Where(...)` (HeaderPage.cs:219-225, BsonDocument.cs:110-120). SysCols/SysIndexes hold that enumerator open across their own `yield return`s, i.e. across arbitrary client-controlled pauses between reader.Read() calls. Collection create/drop mutates the same dictionary at commit time — `h.InsertCollection(name, pageID)` (CollectionService.cs:72) and `h.DeleteCollection(...)` (SnapShot.cs:742) run from TransactionPages.OnCommit inside `lock (_header)` (TransactionService.cs:217, 262). Readers take no such lock: LockService gives writers a *shared* read lock on _transaction (LockService.cs:41-47) plus a per-collection lock, so a writer's header commit runs fully concurrently with a reader's header enumeration. Dictionary.Add/Remove bumps _version, so the reader's enumerator throws. (SysDump.cs:31 and SysPageList.cs:31 have the same exposure through ToDictionary, with a narrower window.)

**Failure.** Thread A: `var r = db.Execute("SELECT * FROM $cols"); while (r.Read()) { /* render a row */ }` over a database with a few dozen collections. Thread B: `db.GetCollection("newlog").Insert(doc)` — the auto-transaction commits and calls InsertCollection, adding a key to _collections. Thread A's next Read() throws `InvalidOperationException: Collection was modified; enumeration operation may not execute` instead of returning the remaining collection names. Same for `SELECT * FROM $indexes` racing any CreateCollection/DropCollection.

**Fix.** Materialize the collection list once under `lock (_header)` (e.g. `_header.GetCollections().ToArray()` inside the lock) before yielding any document, and iterate that array.

### H44. $transactions can trip ENSURE on a concurrently-disposed snapshot, closing the engine and stamping the data file as invalid

`LiteDB/Engine/SystemCollections/SysTransactions.cs:27` · concurrency · rank #61

**Mechanism.** SysTransactions enumerates every registered transaction (line 14, other threads' live transactions) and calls Snapshot.GetWritablePages on each of their snapshots. GetWritablePages begins with `ENSURE(!_disposed, "the snapshot is disposed")` (LiteDB/Engine/Services/SnapShot.cs:111) and ENSURE is NOT [Conditional("DEBUG")] (LiteDB/Utils/Constants.cs:137-148) — it always throws LiteException with ErrorCode INVALID_DATAFILE_STATE (999). TransactionService.Commit() disposes all of its snapshots (TransactionService.cs:277-280) and only afterwards does TransactionMonitor.ReleaseTransaction remove the transaction from the registry (TransactionMonitor.cs:120 disposes, then :109 removes). So there is a wide window in which a transaction is still visible via GetTransactionsSnapshot() while every one of its snapshots already has _disposed == true. The thrown LiteException surfaces inside QueryExecutor's `catch (Exception ex) { _state.Handle(ex); throw ex; }` (QueryExecutor.cs:136-140). EngineState.Handle treats INVALID_DATAFILE_STATE as fatal (EngineState.cs:41-48) and calls LiteEngine.Close(ex), which sets _state.Disposed, tears down monitor/disk/sort/locker AND — because TryCatch.InvalidDatafileState is true — calls _disk.MarkAsInvalidState() (LiteEngine.cs:227-230), which writes byte 1 at HeaderPage.P_INVALID_DATAFILE_STATE into page 0 of the *data file* and FlushToDisk (DiskService.cs:269-294). Nothing in the system-collection path checks transaction.State or snapshot disposal, and $transactions is a read-only diagnostics view that the sibling collections were already hardened for (TransactionRegistry's comment: "readers ... never enumerate a collection that another thread can resize").

**Failure.** Thread A polls diagnostics: `while(true) db.Execute("SELECT * FROM $transactions").ToList();`. Thread B does ordinary writes: `while(true) col.Insert(new BsonDocument{["x"]=1});`. Each insert runs through AutoTransaction -> Commit() (snapshots disposed) -> ReleaseTransaction (registry entry removed). Within seconds thread A samples B's transaction inside that window, GetWritablePages throws LiteException(999, "the snapshot is disposed"), EngineState.Handle closes the whole engine mid-flight (every other in-flight transaction on every thread then fails with ObjectDisposedException, uncommitted work lost, the LiteDatabase instance is permanently dead) and writes the invalid-state flag into the healthy data file. If the connection string has `auto-rebuild=true`, the next Open() sees that byte and runs the destructive Recovery() path (LiteEngine.cs:113-128) — renaming the file to -backup and rebuilding a database that was never corrupt.

**Fix.** Do not call GetWritablePages from diagnostics. Compute modifiedPages from a disposal-safe accessor (or skip snapshots whose transaction.State != Active), iterate a copied snapshot array, and never let a diagnostics-only exception reach EngineState.Handle as INVALID_DATAFILE_STATE.

### H45. SharedEngine.Query leaves the cross-process mutex permanently held (and the engine open) when LiteEngine.Query throws

`LiteDB/Client/Shared/SharedEngine.cs:146` · error-handling · rank #62

**Mechanism.** `Query` calls `OpenDatabase()` (which acquires the named mutex and creates the engine) and then calls `_engine.Query(...)` with no try/catch. Release happens only through the returned `SharedDataReader`'s dispose callback, which is never constructed if `_engine.Query` throws. `LiteEngine.Query` throws *eagerly* on realistic input: `LiteEngine.Query` (LiteDB/Engine/Engine/Query.cs:17-28) throws `ArgumentNullException`/`LiteException` for bad collection names and unregistered `$system` collections, and `QueryExecutor.ExecuteQuery` builds `new BsonDataReader(enumerable,...)` whose constructor calls `_source.MoveNext()` eagerly (LiteDB/Document/DataReader/BsonDataReader.cs:56), running `QueryOptimization.ProcessQuery()` inside the constructor — which throws `LiteException.InvalidExpressionTypePredicate` for a non-predicate WHERE (LiteDB/Engine/Query/QueryOptimization.cs:105) and for `*` in WHERE (line 87). `LiteQueryable.Where` performs no validation, so these reach the engine unchecked.

**Failure.** `using var db = new LiteDatabase(new ConnectionString{Filename="a.db", Connection=ConnectionType.Shared}); try { db.GetCollection("c").Find("$.Name").ToList(); } catch (LiteException) { /* app logs and continues */ }`. The non-predicate expression makes `SplitWherePredicateInTerms` throw inside the eager `MoveNext()`; the exception unwinds out of `SharedEngine.Query` after `OpenDatabase()` succeeded. `_engine` stays non-null and the `Global\...Mutex` stays owned by that thread forever. Any other process that opens a.db in shared mode blocks indefinitely, and `db.Dispose()` releases only one level (and does nothing at all once a second leak has occurred). The same holds for `db.Execute("SELECT $ FROM $indexs")` (typo'd system collection), which throws from `GetSystemCollection`.

**Fix.** Wrap the body in try/catch: on exception, `if (opened) CloseDatabase();` and rethrow — mirroring the pattern already used in `QueryDatabase`/`BeginTrans`.

### H46. Commit()/Rollback() are not mutex-guarded: called from a thread with no transaction they dispose an engine another thread is actively using and call ReleaseMutex without owning the mutex

`LiteDB/Client/Shared/SharedEngine.cs:110` · concurrency · rank #64

**Mechanism.** `Commit`/`Rollback` are public `ILiteEngine` members but never call `OpenDatabase()`; they read `_engine` and then unconditionally run `_transactionRunning = false; CloseDatabase();` in a `finally`. They assume the calling thread is the one that opened the transaction. LiteDB transactions are per-thread (`LiteEngine.Commit` -> `_monitor.GetTransaction(false,false,out _)` returns null for a thread with no transaction and simply returns false), and `LiteDatabase` is documented as thread-safe, so a second thread can legitimately call `db.Commit()`. That call then clears the flag that protects another thread's live transaction and `CloseDatabase()` disposes the engine (`LiteEngine.Close` -> `TransactionMonitor.Dispose` -> `TransactionService.Dispose`, which *discards* dirty pages — no commit), then calls `_mutex.ReleaseMutex()` from a non-owning thread, which throws `ApplicationException` ("Object synchronization method was called from an unsynchronized block of code").

**Failure.** One shared `LiteDatabase` used by two threads. Thread A: `db.BeginTrans(); col.Insert(doc1); col.Insert(doc2);` (mutex owned by A, `_transactionRunning == true`). Thread B (e.g. a `finally { db.Commit(); }` in an unrelated request handler that never began a transaction): `_engine != null` so `_engine.Commit()` returns false for B; the finally sets `_transactionRunning = false` and `CloseDatabase()` disposes the engine, throwing away A's two uncommitted inserts, sets `_engine = null`, then `ReleaseMutex()` throws `ApplicationException` out of B's finally (masking B's original error, if any). Thread A's subsequent `db.Commit()` sees `_engine == null` and returns **false**, so A believes there was nothing to commit: silent loss of an acknowledged-looking transaction, plus the mutex left owned by A.

**Fix.** Guard Commit/Rollback the same way as the other operations (only act when this thread owns the open transaction, e.g. track the transaction's owning thread id and make Commit/Rollback no-ops for other threads), and never dispose the engine / release the mutex from a thread that did not acquire it.

### H47. The shared mutex is thread-affine but is held across a lazily-enumerated reader, so the release can happen on a different thread than the acquire

`LiteDB/Client/Shared/SharedEngine.cs:148` · concurrency · rank #65

**Mechanism.** `Query` acquires the named mutex on the thread that triggers the first `MoveNext()` and releases it from `SharedDataReader.Dispose()` -> `CloseDatabase()` -> `_mutex.ReleaseMutex()`. `LiteQueryable.ToDocuments()` (LiteDB/Client/Database/LiteQueryable.cs:293-302) is an iterator holding `using (var reader = this.ExecuteReader())`, so acquisition happens at the first iteration and release when the enumerator is disposed — i.e. wherever the `foreach` happens to end. A Win32/PAL mutex can only be released by its owning thread; releasing from another thread throws `ApplicationException` and leaves the mutex owned by the original thread forever.

**Failure.** Common async pattern with a shared-mode database: `foreach (var doc in col.FindAll()) { await SendAsync(doc); }`. The first `MoveNext()` runs on thread A (mutex acquired, engine opened); after the first `await` completes, the continuation — and therefore the remaining iterations and the enumerator's `Dispose()` at the end of the `foreach` — run on a different thread-pool thread B. B's `ReleaseMutex()` throws `ApplicationException` (surfacing as a bizarre error from a `foreach` block, after the engine has already been disposed and `_engine` nulled), and `Global\...Mutex` remains owned by thread A for the life of the process, blocking every other process that opens the same file in shared mode.

**Fix.** Do not hold a thread-affine `Mutex` across a lazily enumerated reader; either materialize results while the mutex is held, or use a cross-process primitive that is not thread-owned (e.g. a lock file / semaphore) for shared mode.

### H48. Plain `REBUILD` passes null options and throws NullReferenceException after the engine has already been closed, permanently wedging the connection ✅

`LiteDB/Client/SqlParser/Commands/Rebuild.cs:25` · error-handling · rank #66

**Mechanism.** For the no-argument form the parser sets `options = null` and calls `_engine.Rebuild(null)`. `LiteEngine.Rebuild(RebuildOptions)` (LiteDB/Engine/Engine/Rebuild.cs:19-37) has no null guard: it first calls `this.Close()` — which sets `_state.Disposed = true` and disposes monitor/disk/sortdisk/locker (LiteEngine.cs:180-205) — then calls `rebuilder.Rebuild(options)`, where `RebuildService.Rebuild` dereferences `options.Errors` at line 49 (`new FileReaderV8(_settings, options.Errors)`) for a v8 file, or `options.Collation` at line 58 for a v7 file. Either dereference throws NullReferenceException. Because LiteEngine.Rebuild has no try/catch, the trailing `this.Open()` and `_state.Disposed = false` (lines 32-34) never execute. The public API layer does guard this (`LiteDatabase.Rebuild(RebuildOptions options = null)` does `_engine.Rebuild(options ?? new RebuildOptions())`, LiteDatabase.cs:302-305), proving null is not an accepted engine input; the SQL path bypasses that guard. No test covers SQL REBUILD.

**Failure.** `using var db = new LiteDatabase("data.db"); db.Execute("REBUILD");` -> System.NullReferenceException (not a LiteException), no rebuild performed, and the `LiteDatabase`/engine instance is left with `_state.Disposed == true`, so every subsequent call (`db.GetCollection(...).Insert(...)`, `db.Execute("SELECT 1")`, even `db.Checkpoint()`) throws `LiteException.EngineDisposed()` for the lifetime of the object. The caller must construct a whole new LiteDatabase to recover.

**Fix.** Do not null out `options`; keep the instance built at line 18 (seeded from current password/collation as per the previous finding), or call the parameterless `LiteEngine.Rebuild()` semantics explicitly.

### H49. BsonMapper.GetEntityMapper disposes the CancellationTokenSource whose token the cached EntityMapper keeps, so a concurrent first-time map of the same type can throw ObjectDisposedException

`LiteDB/Client/Mapper/BsonMapper.GetEntityMapper.cs:27` · concurrency · rank #67

**Mechanism.** `using var cts = new CancellationTokenSource();` means the CTS is disposed when GetEntityMapper returns, but cts.Token is stored inside the EntityMapper that stays in the _entities cache forever. EntityMapper.WaitForInitialization (EntityMapper.cs:59-65) first tests IsCancellationRequested (which is safe after Dispose) and then touches `_initializationToken.WaitHandle`, and CancellationTokenSource's WaitHandle getter calls ThrowIfDisposed() and throws ObjectDisposedException. A second thread that obtained the still-unfinished shell mapper from _entities.TryGetValue can observe IsCancellationRequested == false and then reach `.WaitHandle` after the building thread has run its finally (cts.Cancel()) and the using-scope Dispose().

**Failure.** Two threads call `db.GetCollection<Order>()` for the first time simultaneously (LiteCollection's ctor does GetEntityMapper then WaitForInitialization). Thread A adds the empty shell and spends milliseconds in BuildEntityMapper compiling getters/setters. Thread B gets the shell, sees IsCancellationRequested == false, is preempted; A finishes, Cancel()s and Dispose()s the CTS; B resumes at `_initializationToken.WaitHandle` and gets an unhandled `ObjectDisposedException: The CancellationTokenSource has been disposed` out of GetCollection<Order>() instead of a LiteDB error.

**Fix.** Do not hand a token from a CTS that will be disposed to a long-lived cached object. Signal completion with a ManualResetEventSlim (or a volatile flag plus a Monitor) owned by the EntityMapper, or keep the CTS alive for the lifetime of the cached mapper instead of using `using var`.

### H50. Rebuild() on a :memory: or :temp: database destroys all data and then throws "Invalid database"

`LiteDB/Engine/Engine/Rebuild.cs:21` · correctness · rank #68

**Mechanism.** The guard only rejects a null/empty `Filename`, but the sentinel filenames `:memory:` and `:temp:` are non-empty and are NOT OS files. `EngineSettings.CreateDataFactory()` maps them to `new StreamFactory(new MemoryStream(), ...)` / `new StreamFactory(new TempStream(), ...)` with `ownsStream: true` - a *brand new, empty* stream on every call (LiteDB/Engine/EngineSettings.cs:112-118). So: (1) `this.Close()` disposes `DiskService`, which disposes the `StreamFactory`, which disposes the `MemoryStream`/`TempStream` holding the entire database (`StreamFactory.Dispose`: `if (_ownsStream) _stream.Dispose();`, LiteDB/Engine/Disk/StreamFactory/StreamFactory.cs:116-121). The data is now unrecoverable. (2) `new RebuildService(_settings)` calls `ReadFirstBytes()`, which calls `CreateDataFactory()` again and therefore reads a *fresh empty* stream - the 16 KB buffer stays all zeros. `FileReaderV7.IsVersion(zeros)` is false and `FileReaderV8.IsVersion(zeros)` is false (header string mismatch, `buffer[0] != 1`), so the constructor throws `LiteException.InvalidDatabase()` (LiteDB/Engine/Services/RebuildService.cs:36). (3) Because the exception escapes before line 32, `this.Open()` never runs, so the engine is left permanently in `Disposed` state. Even if the reader had succeeded, `File.Move(":memory:", ":memory:-backup")` in `RebuildService.Rebuild` could not work.

**Failure.** `using var db = new LiteDatabase(":memory:"); db.GetCollection<Foo>("foo").Insert(items); var freed = db.Rebuild();` -> `Rebuild` throws `LiteException` "Invalid database", every inserted document is gone (the backing `MemoryStream` was disposed by `Close()`), and the `LiteDatabase` instance is dead: any subsequent call hits `_state.Validate()` and throws `EngineDisposed`. Identical behaviour for `new LiteDatabase(":temp:")`. Compare with a stream-backed database (`new LiteDatabase(memoryStream)`), where `Filename` is null and `Rebuild()` correctly returns 0 without touching anything.

**Fix.** Widen the guard to reject non-file sources, e.g. `if (string.IsNullOrEmpty(_settings.Filename) || _settings.Filename == ":memory:" || _settings.Filename == ":temp:" || _settings.DataStream != null) return 0;` - and perform the check before `this.Close()` in every case so a rejected rebuild never tears the engine down.

### H51. A zeroed verification block makes AesStream accept any password and re-key the file; on read-only streams the same path throws NullReferenceException

`LiteDB/Engine/Disk/Streams/AesStream.cs:121` · security · rank #69

**Mechanism.** For an existing file, if bytes 32..64 read as all zero the constructor flips `isNew` back to true, which skips password verification completely and instead re-writes the verification block with the key derived from whatever password was supplied (lines 129-138). Nothing else in the constructor validates the password, so any password is accepted and the engine proceeds to decrypt (and later encrypt) real pages with the wrong key. On top of that, `_writer` is null for every non-writable stream (line 109: `_writer = _stream.CanWrite ? ... : null`), which is the case for all pooled reader streams (FileStreamFactory.GetStream(canWrite:false) opens with FileAccess.Read) and for ReadOnly connections, so line 133 dereferences null on that path.

**Failure.** An attacker with only write access to foo.db (no password) zeroes the 32 bytes at offset 32. They then open the database with the password "x": verification is skipped, the block is re-written under key("x"), and LiteDB starts writing new pages encrypted under key("x") into a file whose existing pages are under the owner's key - the file becomes permanently mixed-key and unrecoverable, and the owner's correct password is now rejected with InvalidPassword because the verification block was re-keyed. In the same zeroed state, a reader stream (any query on a ReadOnly connection, or RebuildService.ReadFirstBytes, which calls GetStream(false, true)) hits line 133 with `_writer == null` and surfaces NullReferenceException instead of any diagnosable error.

**Fix.** Only take the re-initialisation path when the file really is new (length < PAGE_SIZE); otherwise treat a zeroed verification block as a corrupt/unverifiable header, and guard the write path on `_writer != null` with an explicit exception.

### H52. Datafile encryption uses AES-ECB with one key for the whole file and no integrity check; the derived IV is never used

`LiteDB/Engine/Disk/Streams/AesStream.cs:92` · security · rank #70

**Mechanism.** `_aes.Mode = CipherMode.ECB` with `PaddingMode.None` means every 16-byte plaintext block is encrypted independently under a single file-wide key, so encryption is deterministic and position-independent: equal plaintext blocks always produce equal ciphertext blocks anywhere in the file. The IV derived on line 99 (`_aes.IV = pdb.GetBytes(16)`) is ignored by ECB, which makes the construction look stronger than it is. There is also no MAC anywhere over page contents or over the salt page, so ciphertext modification is undetectable.

**Failure.** An attacker who obtains a copy of an encrypted .db file (backup, stolen disk, shared volume) learns plaintext structure without the password: identical documents/index pages/zero-filled page tails share identical 16-byte ciphertext blocks, so duplicate records and known 16-byte-aligned plaintext are recognisable, and page-to-page similarity is measurable. With write access, the attacker can splice ciphertext: copying 16-byte blocks (or whole 8192-byte pages) between offsets makes LiteDB decrypt them as authentic data - e.g. relocating an old version of a page over a newer one to roll a record back - with no integrity failure reported.

**Fix.** Switch to a per-page-offset keystream (AES-CTR with the page position as the counter block) plus a per-page MAC, or document the encryption as obfuscation only.

### H53. Page segment access validates position and length independently but never position+length, letting a malformed page produce BufferSlices and raw Array.Fill/BlockCopy that run past PAGE_SIZE into neighbouring pages of the shared cache array

`LiteDB/Engine/Pages/BasePage.cs:317` · corruption · rank #72

**Mechanism.** `IsValidPos` (line 726) only checks `position >= 32 && position < PAGE_SIZE - FooterSize`; `IsValidLen` (line 731) only checks `0 < length <= PAGE_SIZE - 32 - FooterSize`. Neither the pair of ENSUREs in `Get` (313-314), nor those in `Delete` (418-419), nor `Update` (488-489), nor `Defrag` (583/603) ever checks the *sum* `position + length <= PAGE_SIZE - FooterSize`. `BasePage(PageBuffer)` (198-224) reads ItemsCount/UsedBytes/FragmentedBytes/NextFreePosition/HighestIndex straight off disk with no cross-validation, and `Snapshot.ReadPage<T>` (LiteDB/Engine/Services/SnapShot.cs:236-280) adds only a PageType check — LiteDB has no per-page checksum. So a slot whose position+length straddles the page end is accepted. The consequences are not bounded to the page, because `PageFramePool` allocates one `new byte[PAGE_SIZE * segmentSize]` for 8 or 128 pages (LiteDB/Engine/Disk/PageFramePool.cs:198) and `BufferSlice.Slice`/`OwnedSlice` do no bounds check at all (LiteDB/Utils/BufferSlice.cs:129-141). Note that `Delete` deliberately bypasses the one bounds-checked primitive: `BufferSlice.Clear(offset,count)` has `ENSURE(offset + count <= this.Count)`, but line 430 calls `_buffer.Array.Fill(0, _buffer.Offset + position, length)`, a raw loop over the pool array (LiteDB/Utils/Extensions/BufferExtensions.cs:61). `Defrag`'s `System.Buffer.BlockCopy` (611) and `Array.Fill` (629) are equally unchecked.

**Failure.** Take a DataPage whose footer slot 0 holds position = 8180, length = 100, with ItemsCount = 1, HighestIndex = 0 (FooterSize = 4). IsValidPos(8180): 8180 >= 32 && 8180 < 8188 -> true. IsValidLen(100): 100 > 0 && 100 <= 8156 -> true. Both ENSUREs pass. `Get(0)` then returns `OwnedSlice(8180, 100)`, a slice spanning bytes 8180..8279 of the pool array — 88 bytes of it belong to the NEXT page sharing that 128-page byte[]. Reads: the document returned to the user contains 88 bytes of an unrelated page (wrong query results / cross-collection data disclosure). Writes: `DataService.Update` -> `DataPage.UpdateBlock` -> `BasePage.Update` returns the same out-of-range slice and BufferWriter overwrites the neighbouring page's header (P_PAGE_ID/P_PAGE_TYPE/P_PREV_PAGE_ID...) in cache; if that neighbour is dirty in the same transaction it is then flushed, turning a single bad footer slot into on-disk corruption of a second page. Deletes: `page.DeleteBlock(0)` -> line 430 zeroes 88 bytes of the neighbour's header (PageID 0, PageType Empty). If the page happens to be the last frame in its pool segment the same call throws IndexOutOfRangeException instead.

**Fix.** Validate the segment as a whole, e.g. `IsValidSegment(position, length) => position >= PAGE_HEADER_SIZE && length > 0 && position + length <= PAGE_SIZE - this.FooterSize`, and use it in Get/Update/Delete/Defrag. Also route Delete's and Defrag's zeroing through the bounds-checked `BufferSlice.Clear` instead of `_buffer.Array.Fill`, and range-check `next + length` inside the Defrag loop.

### H54. LIKE with a null or non-string parameter throws NullReferenceException/InvalidCastException when an index exists

`LiteDB/Engine/Query/IndexQuery/IndexLike.cs:19` · error-handling · rank #73

**Mechanism.** IndexLike never validates that the pattern is a string. BsonValue.AsString is `(string)this.RawValue` (BsonValue.cs:233). For a BsonValue of type Null it yields null, and StringExtensions.SqlLikeStartsWith dereferences it immediately (`var len = str.Length;`, StringExtensions.cs:175) -> NullReferenceException; the implicit string->BsonValue conversion in IndexCost.CreateIndex keeps the null (BsonValue.cs:80-84, Structures/IndexCost.cs:87). For a numeric BsonValue the `(string)` cast on a boxed int throws InvalidCastException. ChooseIndex accepts the predicate because the right side is a parameter with Fields.Count == 0 (IsValue), so the crash happens during query planning. Without an index on the field the same query evaluates BsonExpressionOperators.LIKE, which just returns false for non-string operands, so the exception only appears once the index exists.

**Failure.** The documented parameterised form from LiteDB.Tests/Issues/Issue2506_Tests.cs, `fileStorage.Find("_id LIKE @0", pattern)`, with pattern == null (e.g. a nullable search box value). The PK index always matches "$._id", IndexCost builds IndexLike with a BsonType.Null value, and the call throws NullReferenceException out of QueryOptimization.ProcessQuery instead of returning an empty result. Passing an int (`Find("$.name LIKE @0", 5)`) throws InvalidCastException the same way.

**Fix.** In IndexLike (or IndexCost.CreateIndex) fall back to a non-matching plan or leave the term as a filter when the pattern value is not BsonType.String, instead of dereferencing/casting it.

### H55. FileReaderV7.ReadExtendData() loops forever (and grows a MemoryStream without bound) on a truncated or cyclic extend chain

`LiteDB/Engine/FileReader/FileReaderV7.cs:387` · infinite-loop · rank #75

**Mechanism.** The chain walk `while (extendPageID != uint.MaxValue)` has no cycle detection, no visited set and no iteration cap, and each iteration appends itemCount bytes into an in-memory MemoryStream. Two independent inputs make it non-terminating: (a) any extend page whose nextPageID points back to an already-visited extend page; (b) the off-by-one EOF guard in ReadPage (line 189: `if (pageID * V7_PAGE_SIZE > _stream.Length) return null;` - not `(pageID+1)*...`) combined with the reused, never-cleared `_buffer` field: for pageID == _stream.Length / 4096 the guard passes, `_stream.Read` returns 0 bytes and the *previous* page's bytes are re-parsed, so the last extend page in a truncated file yields itself forever. Nothing in FileReaderV7 catches exceptions, so the only exit is the MemoryStream exceeding int.MaxValue (IOException 'Stream was too long') or OutOfMemoryException after ~2 GB.

**Failure.** A LiteDB v4 file whose document spanned extend pages 9->10 is truncated to exactly 10 pages (40960 bytes), e.g. by an interrupted file copy. Opening it with `Upgrade=true` (LiteEngine ctor -> TryUpgrade -> Recovery -> RebuildService -> FileReaderV7) reaches ReadExtendData(9): page 9 is pageType 5 with nextPageID = 10; ReadPage(10) passes `40960 > 40960 == false`, reads 0 bytes, and returns page 9 again; nextPageID is 10 again. The constructor never returns - the process spins reading the same page and allocating memory until it exhausts RAM (or throws IOException after ~2 GB). A hostile .db file with a 2-page extend cycle produces the same hang with no truncation at all.

**Fix.** Track visited extend pageIDs in a HashSet<uint> (bail out on repeat), cap the accumulated length at MAX_DOCUMENT_SIZE, and fix the ReadPage guard to `(pageID + 1) * V7_PAGE_SIZE > _stream.Length`.

### H56. FileReaderV8.GetDocuments() extend-block walk has no cycle detection - self-referencing data block allocates until OOM/IOException and aborts the rebuild

`LiteDB/Engine/FileReader/FileReaderV8.cs:176` · infinite-loop · rank #76

**Mechanism.** `while (nextBlock.IsEmpty == false)` follows DataBlock.P_NEXT_BLOCK addresses read straight from the file with no visited-set, no page-count cap and no MAX_DOCUMENT_SIZE cap, appending `data.Count` bytes to a MemoryStream each iteration. The in-loop ENSUREs (PageType.Data, matching ColID, ItemsCount > 0, length > 0, extend == true) are all satisfied by a block that points at itself, so the loop never advances. It ends only when MemoryStream.EnsureCapacity overflows int (IOException 'Stream was too long') or the process runs out of memory. Because HandleError rethrows `ex is IOException` (line 571-577), the IOException escapes the iterator into LiteEngine.RebuildContent, which calls this.Close(ex) and rethrows - so the whole rebuild fails instead of skipping one bad document.

**Failure.** A corrupt (or attacker-supplied) v8 file where data page 40, slot 3 has extend=true and P_NEXT_BLOCK = (40, 3), reachable from a non-extend first block. db.Rebuild() (or auto-rebuild on open) reads page 40/slot 3 repeatedly, appending ~8 KB per iteration; after ~260k iterations (seconds of CPU) the process has allocated ~2 GB and either dies with OutOfMemoryException on a memory-limited host or throws IOException, which HandleError rethrows and which aborts the entire recovery - no collection is recovered even though the rest of the file was readable.

**Fix.** Keep a HashSet<PageAddress> of visited blocks inside the merge loop and abort that document (via HandleError) on a repeat, and cap the accumulated length at MAX_DOCUMENT_SIZE.

### H57. _type deny-list is trivially bypassed: blocked gadget types can be reached as generic arguments, dictionary value types, or declared member types ⚠️ needs more evidence

`LiteDB/Client/Mapper/TypeNameBinder/DefaultTypeNameBinder.cs:67` · security · rank #77

**Mechanism.** DefaultTypeNameBinder.GetType is the only place the vulnerable-type deny-list is consulted, and it only compares the *exact* FullName of the top-level type named by a document's `_type` field. BsonMapper.Deserialize (BsonMapper.Deserialize.cs:194-207) then instantiates that type and recursively deserializes its members/elements with the type taken from reflection metadata (member DataType, dictionary value type, generic argument) — those recursive calls never go through ITypeNameBinder and therefore never hit the deny-list. Wrapping a blocked type in a closed generic changes FullName (`System.Collections.Generic.Dictionary`2[[System.String, ...],[System.Windows.Data.ObjectDataProvider, ...]]`) so `_disallowedTypeNames.Contains(type.FullName)` is false, while the wrapper's value type is still instantiated and property-populated by DeserializeDictionary -> Deserialize(valueType, ...) -> GetEntityMapper/Reflection.CreateInstance/DeserializeObject. Separately, four deny-list entries ("System.Core", "System.Data", "System.Windows.Forms", "System.Management.Automation") are assembly names, not type FullNames, so they can never match anything and block nothing.

**Failure.** An entity has an `object` or `Dictionary<string, object>` property (the deny-list's whole purpose is to protect exactly this case). A document in the .db file (or inserted by a lower-privileged writer) stores for that field `{ "_type": "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Windows.Data.ObjectDataProvider, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35]], mscorlib", "k": { "ObjectInstance": ..., "MethodName": "..." } }`. GetType() resolves the Dictionary wrapper, FullName does not match the deny-list, `typeof(object).IsAssignableFrom(...)` passes, and Deserialize(typeof(ObjectDataProvider), doc) then constructs the explicitly-blocked ObjectDataProvider and drives its setters (MethodName/ObjectInstance trigger DataSourceProvider.Refresh, i.e. method invocation) with attacker-chosen values. The same wrapper trick re-enables every other name in the list.

**Fix.** Walk the whole resolved type graph before accepting it (GetGenericArguments/GetElementType recursively), and also apply the check whenever a type is about to be instantiated in Deserialize (member DataType, dictionary key/value type, array element type), not only when it came from `_type`. Match on assembly name as well as FullName (or better, invert to an opt-in allow-list of types/assemblies the application expects).

## Medium

75 findings, one line each. Mechanism and failure scenario for every one of these is in
`docs/audit/findings-full.json` alongside this report.

| # | Location | Category | Finding |
|---|---|---|---|
| 78 | `Document/BsonValue.cs:473` | overflow | Mixed-type numeric arithmetic routes through Convert.ToDecimal, throwing OverflowException for doubles outside decimal range |
| 80 | `Engine/Engine/Sequence.cs:40` | overflow | Int32 auto-id silently wraps to a negative value via an unchecked (int) cast |
| 82 | `Engine/Services/SnapShot.cs:633` | durability | Deleted-page chain is never linked into the header free list when a safepoint flushes the chain's tail page, permanently leaking every page freed by a large transaction |
| 83 | `Engine/Services/IndexService.cs:320` | resource-leak | IndexService.DropIndex frees index nodes but never reclaims the pages, permanently leaking them from the file |
| 85 | `Engine/Services/SnapShot.cs:219` | correctness | Snapshot local page cache is keyed only by pageID and cast unchecked, so a page first fetched as BasePage throws InvalidCastException when later fetched as DataPage/IndexPage ⚠️ |
| 86 | `Document/BsonValue.cs:588` | api-contract | Document/Array comparison silently drops the Collation argument, so nested strings are always compared ordinally |
| 87 | `Document/Expression/Methods/Aggregate.cs:30` | correctness | MIN/MAX compare with hard-coded ordinal collation, disagreeing with the database collation and ORDER BY |
| 88 | `Document/BsonValue.cs:678` | api-contract | Equals/GetHashCode contract violation: values that compare equal hash differently, breaking Distinct() over index keys |
| 89 | `Engine/Disk/Serializer/BufferReader.cs:406` | error-handling | ReadDocument/ReadArray accept an out-of-range BSON length prefix and silently return an empty document instead of reporting corruption |
| 90 | `Engine/Disk/Streams/AesStream.cs:151` | correctness | AesStream derives its blank-page sentinel from an uninitialized ArrayPool buffer, silently disabling zeroed-page detection |
| 92 | `Engine/Disk/Streams/AesStream.cs:94` | security | Key derivation uses Rfc2898DeriveBytes defaults: 1000 PBKDF2 iterations with HMAC-SHA1 |
| 93 | `Client/Storage/LiteFileStream.Write.cs:22` | error-handling | LiteFileStream.Flush/Write dereference the null write buffer on read-mode streams |
| 95 | `Client/Database/LiteDatabase.cs:398` | durability | Restoring the CHECKPOINT pragma before engine close can disable the shutdown checkpoint and discard the in-memory WAL |
| 96 | `Client/Database/LiteQueryable.cs:456` | api-contract | LiteQueryable.Into() permanently leaves Query.Into set, so reusing the queryable re-inserts the whole result set |
| 97 | `Client/Database/LiteRepository.cs:398` | api-contract | LiteRepository disposes a caller-supplied ILiteDatabase it does not own, and never null-checks it |
| 98 | `Client/Database/Collections/Aggregate.cs:165` | correctness | Min<K>/Max<K> throw NullReferenceException when the extreme key is a BSON null and K is a non-nullable value type |
| 99 | `Engine/Engine/Index.cs:40` | api-contract | EnsureIndex silently reports success for a name already taken by a vector index, so no B-tree index is created |
| 100 | `Engine/Pages/HeaderPage.cs:140` | api-contract | HeaderPage.LoadPage() replaces the Pragmas instance, orphaning the reference LockService and SortDisk captured at engine open, so later PRAGMA changes are silently ignored |
| 101 | `Engine/Services/VectorIndexService.cs:488` | efficiency | Upsert always runs a full-graph BFS to locate the node, making bulk insert O(N^2) and allocating a queue proportional to 32*N per operation |
| 102 | `Engine/Services/VectorIndexService.cs:685` | correctness | External vector payloads are written as non-extend DataBlocks in the collection's data pages, so Rebuild/Recovery parses raw float bytes as BSON documents |
| 103 | `Client/Vector/LiteQueryable.Vector.cs:33` | correctness | String-overload WhereNear/TopKNear build `$.{field}` by concatenation, so a non-word field name silently parses into a different expression and matches every document |
| 104 | `Engine/Query/Query.cs:119` | api-contract | Query.ToSQL emits the WHERE clause after ORDER BY / LIMIT / OFFSET / FOR UPDATE, producing SQL that LiteDB itself cannot parse |
| 105 | `Client/Vector/LiteQueryable.Vector.cs:89` | error-handling | TopKNear mutates the shared Query before the OrderBy that can throw, leaving a half-configured vector query behind a misleading 'Multiple OrderBy calls' error |
| 106 | `Engine/SystemCollections/SysOpenCursors.cs:16` | concurrency | $open_cursors / $snapshots / $transactions enumerate other threads' live List/Dictionary, throwing "Collection was modified" |
| 107 | `Engine/SystemCollections/SysIndexes.cs:20` | correctness | $indexes and $page_list dereference a null CollectionPage when a collection is dropped mid-enumeration |
| 108 | `Engine/SystemCollections/SysFileCsv.cs:180` | correctness | CSV export writes JSON-escaped strings, so any value containing a quote, delimiter or newline corrupts the file |
| 109 | `Engine/SystemCollections/SysDatabase.cs:33` | overflow | $database reports dataFileSize/logFileSize through an unchecked (int) cast of a long |
| 110 | `Document/BsonValue.cs:138` | correctness | BsonValue(object) can produce Document/Array-typed values that are not BsonDocument/BsonArray, making AsDocument/AsArray null and every consumer throw NullReferenceException |
| 111 | `Document/BsonValue.cs:368` | api-contract | UInt64 implicit conversions are asymmetric: BsonValue stores a Double but the reverse cast unboxes a UInt64, throwing InvalidCastException on round trip |
| 112 | `Document/BsonValue.cs:717` | correctness | Unbounded recursion in GetBytesCount/ToString for self-referencing BsonDocument kills the process with StackOverflowException |
| 113 | `Document/Expression/Parser/BsonExpressionParser.cs:854` | untrusted-input | Recursive-descent expression parser has no nesting-depth limit: a few thousand nested parentheses/brackets/braces terminate the process with StackOverflowException |
| 114 | `Utils/Tokenizer.cs:467` | untrusted-input | Tokenizer recurses once per `--` comment line, so a script with many comment lines overflows the stack |
| 115 | `Document/Expression/Parser/BsonExpressionParser.cs:135` | error-handling | `ANY`/`ALL` followed by AND/OR/VECTOR_SIM builds an operator key absent from the precedence table, and the reduce loop walks past the end of the table (ArgumentOutOfRangeException) |
| 116 | `Document/Expression/Parser/BsonExpressionParser.Functions.cs:93` | error-handling | ParseFunction does not check that a MAP/FILTER/SORT overload was found, so a wrong argument count throws ArgumentNullException from Expression.Call |
| 117 | `Document/Expression/Parser/BsonExpressionParser.cs:1410` | correctness | IIF immutability is computed with C# `&&`/`\|\|` precedence, marking volatile expressions as immutable and letting them be used as index expressions |
| 118 | `Utils/Tokenizer.cs:637` | correctness | Tokenizer silently deletes unrecognised string escape sequences instead of erroring or keeping them literally |
| 119 | `Document/Expression/Methods/String.cs:273` | null-propagation | MATCH returns CLR null instead of BsonValue.Null for non-string input |
| 120 | `Document/Expression/Methods/Math.cs:40` | error-handling | ABS and ROUND throw unhandled exceptions on edge-value arguments instead of returning Null |
| 123 | `Document/Expression/Methods/Date.cs:123` | overflow | DATEDIFF('s'\|'h'\|'d') and DATEADD overflow/throw on wide but realistic date ranges |
| 125 | `Document/Json/JsonReader.cs:126` | bounds | Empty JSON object key throws IndexOutOfRangeException out of the parser and breaks LiteDB's own JSON export/import round trip |
| 127 | `Document/Json/JsonWriter.cs:266` | correctness | JsonWriter does not escape document keys, producing invalid JSON and allowing structural injection into exported JSON |
| 128 | `Document/Json/JsonWriter.cs:82` | correctness | Double values are serialized with a fixed 9-decimal format, silently zeroing small magnitudes and truncating precision |
| 129 | `Document/Json/JsonReader.cs:92` | overflow | Integer literals larger than Int64.MaxValue raise an unhandled OverflowException from the JSON parser |
| 130 | `Document/Json/JsonReader.cs:176` | error-handling | Extended data types pass unvalidated token text to framework constructors, leaking FormatException/ArgumentException/ArgumentNullException from the parser |
| 131 | `Document/Json/JsonReader.cs:179` | correctness | $date is parsed with the ambient CurrentCulture although it is always written with the invariant ISO-8601 round-trip format |
| 132 | `Client/Mapper/Reflection/Reflection.cs:39` | concurrency | Reflection.CreateInstance reads the shared ctor cache Dictionary outside the lock that guards its writes |
| 133 | `Client/Mapper/BsonMapper.Serialize.cs:143` | correctness | Dictionary key/value types are taken from the type's own generic arguments, so a single-type-parameter Dictionary subclass throws IndexOutOfRangeException on both serialize and deserialize |
| 134 | `Client/Mapper/BsonMapper.Deserialize.cs:241` | correctness | Deserializing into a non-generic subclass of Dictionary<K,V> keeps keyType=object, producing string keys that the target dictionary rejects |
| 135 | `Client/Mapper/BsonMapper.GetEntityMapper.cs:213` | correctness | Constructor parameter matching uses culture-sensitive ToLower(), so immutable entities fail to deserialize under Turkish/Azeri cultures |
| 136 | `Client/Mapper/BsonMapper.Serialize.cs:222` | correctness | DateTimeOffset dictionary keys are written with CurrentCulture but always parsed invariantly, making the documents unreadable |
| 137 | `Client/Mapper/Linq/LinqExpressionVisitor.cs:617` | correctness | Bitwise & and \| are translated to logical AND/OR, producing an InvalidCastException at query execution |
| 138 | `Client/Mapper/Linq/LinqExpressionVisitor.cs:634` | correctness | _dbRefType leaks past a terminal DbRef member access and rewrites the root document's _id to $id |
| 139 | `Client/Shared/SharedEngine.cs:114` | durability | Commit()/Rollback() dispose the engine in their finally when the engine throws, silently discarding a still-active transaction and making retry impossible |
| 140 | `Client/Shared/SharedDataReader.cs:51` | error-handling | SharedDataReader.Dispose leaks the cross-process mutex if the inner reader's Dispose throws |
| 141 | `Client/Shared/SharedMutexNameFactory.cs:36` | api-contract | Long database paths on Linux/macOS bypass the SHA-1 mutex-name fallback and fail mutex creation ⚠️ |
| 142 | `Client/SqlParser/SqlParser.cs:28` | api-contract | Everything after the first statement's `;` is silently discarded — multi-statement SQL executes only the first command with no error |
| 143 | `Client/SqlParser/SqlParser.cs:34` | correctness | Statement and auto-id keyword dispatch uses culture-sensitive ToUpper(), breaking lowercase `insert`/`begin`/`commit`/`rebuild`/`checkpoint`/`explain` under Turkish-family cultures |
| 144 | `Client/SqlParser/Commands/Rollback.cs:18` | correctness | BEGIN/COMMIT/ROLLBACK silently swallow an unrecognized trailing clause and execute anyway |
| 145 | `Utils/Collation.cs:83` | api-contract | Collation implements IEqualityComparer<BsonValue> with a hash code that contradicts its Equals |
| 146 | `Utils/Extensions/DictionaryExtensions.cs:168` | error-handling | DictionaryExtensions.GetValue swallows every exception and discards the original as inner, hiding the real connection-string error |
| 147 | `Client/Mapper/BsonMapper.GetEntityMapper.cs:41` | correctness | On a mapping failure the half-built EntityMapper is removed from the cache but a concurrent thread keeps using it, silently serializing documents with missing fields |
| 148 | `Document/Expression/Parser/BsonExpressionOperators.cs:295` | bounds | ARRAY_INDEX passes an unclamped negative index straight to the array indexer (`arr.Count > idx` accepts negative idx) |
| 149 | `Engine/Sort/SortContainer.cs:70` | error-handling | SortContainer.Insert validates key length after writing it, so oversized sort keys surface as ArgumentOutOfRangeException instead of the intended LiteException |
| 151 | `Engine/Query/Pipeline/GroupByPipe.cs:241` | memory | Sorting a grouped projection buffers every group and its sort key in memory with no spill path |
| 152 | `Engine/FileReader/FileReaderV7.cs:193` | correctness | FileReaderV7.ReadPage() ignores the short-read count and reuses an uncleared buffer, parsing a mix of two pages |
| 153 | `Engine/FileReader/FileReaderV7.cs:148` | error-handling | FileReaderV7 dereferences ReadPage()'s null result in three places - NullReferenceException aborts the v4 upgrade |
| 154 | `Engine/FileReader/FileReaderV7.cs:335` | bounds | FileReaderV7.ReadPage() uses unvalidated length/level fields from the file: negative array size and out-of-range copies |
| 155 | `Engine/FileReader/FileReaderV7.cs:54` | error-handling | FileReaderV7.Open() throws NullReferenceException when page 0 is not parsed as a header page |
| 156 | `Engine/FileReader/FileReaderV8.cs:202` | bounds | FileReaderV8 extend-block loop skips the footer position/length validation applied to the first block |
| 157 | `Engine/Engine/Recovery.cs:19` | api-contract | Recovery ignores EngineSettings.ReadOnly and destructively renames/replaces a read-only database |
| 158 | `Engine/Engine/Recovery.cs:28` | error-handling | Auto-rebuild on a stream-backed database fails with ArgumentNullException from Path.Combine |
| 159 | `Engine/Disk/DiskService.cs:322` | error-handling | ReadFull turns a legal short read into a bogus 'invalid datafile state' instead of completing the page read |
| 160 | `Engine/Services/LockService.cs:101` | api-contract | Checkpoint() called from inside an explicit transaction throws LockRecursionException instead of a LiteDB error |
| 167 | `Engine/Pages/CollectionPage.cs:207` | correctness | CollectionIndex slot allocation overflows a byte after index churn, giving two live indexes the same slot and corrupting the per-document index node chain |
| 168 | `Engine/Disk/Streams/AesStream.cs:248` | api-contract | AesStream.Seek adds the page offset for every origin and returns the underlying stream position instead of the logical one |

## Low

31 findings, one line each. Mechanism and failure scenario for every one of these is in
`docs/audit/findings-full.json` alongside this report.

| # | Location | Category | Finding |
|---|---|---|---|
| 169 | `Engine/Pages/BasePage.cs:647` | denial-of-service | GetFreeIndex's byte loop wraps around forever when HighestIndex == byte.MaxValue, hanging the writer thread while it holds the collection write lock |
| 170 | `Engine/Disk/Serializer/BufferReader.cs:346` | bounds | ReadBytes/ReadString allocate directly from an unvalidated 32-bit on-disk length, allowing OutOfMemoryException on a corrupt file |
| 171 | `Utils/Extensions/BufferSliceExtensions.cs:110` | bounds | BufferSliceExtensions.ReadVector trusts the on-disk element count with no bound against the slice, and page slices share one multi-page array so an overread silently returns other pages' bytes |
| 172 | `Engine/Structures/VectorIndexNode.cs:148` | bounds | VectorIndexNode trusts on-disk neighbour-count and inline-dimension counts, reading past the node segment |
| 173 | `Engine/Structures/PageBuffer.cs:82` | error-handling | PageBuffer finalizer throws LiteException, terminating the process when a cache with pinned frames is collected without disposal |
| 174 | `Engine/LiteEngine.cs:182` | concurrency | Close()'s disposed guard is a non-atomic check-then-set, so two concurrent closes can interleave and skip the invalid-datafile marker |
| 175 | `Engine/LiteEngine.cs:257` | api-contract | LiteEngine.Checkpoint() skips the engine-state check and operates on disposed services |
| 176 | `Engine/Disk/DiskService.cs:271` | error-handling | MarkAsInvalidState can stall error-close for 60 seconds and then silently fail to persist the recovery marker |
| 177 | `Engine/Disk/DiskService.cs:403` | api-contract | Dispose materialises the log writer only to read Length, deleting the log file even for a ReadOnly engine |
| 178 | `Engine/Disk/DiskService.cs:129` | overflow | MAX_ITEMS_COUNT is computed in wrapping uint arithmetic, producing a near-zero loop limit on very large databases |
| 179 | `Engine/Services/WalIndexService.cs:76` | durability | WalIndexService.Clear() resets transaction-ID and read-version bookkeeping before the log truncation that justifies it ⚠️ |
| 180 | `Engine/Services/SnapShot.cs:101` | error-handling | Snapshot constructor cleanup releases the collection write lock outside a finally, so a failure while discarding pages wedges the collection permanently |
| 181 | `Engine/Services/SnapShot.cs:664` | correctness | DropCollection overwrites the transaction's existing deleted-page chain instead of appending to it |
| 182 | `Engine/Pages/BasePage.cs:165` | correctness | The "content area must be zero" DEBUG assertions skip the last page byte, which is the high byte of slot 0's segment position |
| 183 | `Engine/Disk/Serializer/BufferReader.cs:243` | api-contract | BufferReader.ReadDateTime applies the utcDate flag inverted relative to BsonElementReader |
| 184 | `Engine/Services/VectorIndexService.cs:97` | error-handling | Unvalidated VectorDistanceMetric is persisted and later throws ArgumentOutOfRangeException out of the engine, wedging the index |
| 185 | `Client/Storage/LiteStorage.cs:236` | durability | LiteStorage.Delete removes the file document before its chunks, orphaning unreachable chunk documents |
| 186 | `Engine/Engine/Collection.cs:82` | correctness | RenameCollection leaves the auto-id sequence cache keyed to the old collection name |
| 187 | `Client/Database/Collections/Find.cs:30` | efficiency | Find/FindAll add the collection's includes twice, doubling include work per document |
| 188 | `Engine/Sort/SortService.cs:60` | efficiency | SortService allocates an 800 KB LOH buffer per sort operation while its ArrayPool field is never used |
| 189 | `Engine/Sort/SortDisk.cs:101` | resource-leak | SortDisk.Dispose skips temp-file deletion if closing the stream pool throws |
| 191 | `Document/Json/JsonReader.cs:38` | api-contract | Deserialize and DeserializeArray silently ignore trailing content after the first JSON value |
| 192 | `Client/SqlParser/Commands/Select.cs:163` | error-handling | LIMIT/OFFSET use Convert.ToInt32 on the raw token, so a large integer literal escapes as System.OverflowException instead of a parse error |
| 193 | `Client/SqlParser/Commands/Rebuild.cs:45` | correctness | REBUILD with an options document never validates the statement terminator, so trailing input is silently ignored |
| 194 | `Client/SqlParser/Commands/Rebuild.cs:49` | overflow | REBUILD result truncates the long byte-difference to int, reporting a wrong or negative saving for large files |
| 195 | `Engine/Query/Query.cs:135` | correctness | ToSQL formats the vector target and threshold with the current culture and re-appends a predicate already present in Where |
| 196 | `Utils/Extensions/BufferSliceExtensions.cs:137` | bounds | BufferSliceExtensions.ReadCString mixes slice-relative and array-absolute indices and computes a nonsensical length |
| 198 | `Engine/FileReader/Legacy/ByteReader.cs:157` | bounds | Legacy v7 ByteReader.ReadCString indexes the buffer before its bounds check, so the intended fallback is dead code |
| 199 | `LiteDB.Shell/Shell/InputCommand.cs:40` | dos | LiteDB.Shell hangs forever and grows a string without bound when a non-terminated statement reaches EOF or --exit |
| 202 | `.github/scripts/test-crossuser-windows.ps1:13` | security | CI helper script hardcodes a local-account password and creates interactive Windows accounts that survive an interrupted run |
| 203 | `LiteDB.Shell/Commands/Ed.cs:21` | security | Shell 'ed' command writes the last command - which may contain the database password - to a predictable path in the shared temp directory |

## Cross-cutting themes

Fixing the pattern is usually cheaper than fixing the instances.

Fourteen patterns explain most of the 203 findings; fixing the pattern is usually cheaper than fixing the instances.

1. ENSURE is not [Conditional], so internal invariant checks throw LiteException(INVALID_DATAFILE_STATE) in release builds, and EngineState.Handle escalates that to LiteEngine.Close + MarkAsInvalidState. This converts ~a dozen assertion sites into whole-engine shutdowns that stamp a healthy data file as invalid: [3] [6] [19] [20] [24] [33] [39] [42] [55] [93]. Mitigating factor verified: EngineSettings.AutoRebuild defaults to false, and LiteEngine.Open ignores the marker unless AutoRebuild is set, which caps the destructive tail of these.

2. The single-argument BsonValue.CompareTo hardcodes Collation.Binary while the rest of the engine threads the configured collation, so index scans, relational operators, aggregates and nested values disagree with =, ORDER BY and index seeks: [64] [66] [103] [104] [120]. Related contract breaks in the same type: [101] (CompareTo is not antisymmetric for documents with disjoint keys - verified), [105] (Equals/GetHashCode disagree), [177] (Collation's IEqualityComparer).

3. Index/sort-key framing has three readers with incompatible rules - ExtendedLengthHelper's 6-bit type mask, BufferReader.ReadIndexKey's unmasked type plus 8-bit length, and BufferSliceExtensions.ReadIndexKey's extended-length decode. Groups: [25]/[70] (canonical [25]), [26]/[172] (canonical [172]), [31]/[72] (canonical [72]).

4. The vector feature was grafted onto B-tree assumptions and produces the single largest defect cluster: the MAX_INDEX_LENGTH=1400 free-list threshold ([6]/[33]/[42], canonical [42]), BsonType.Vector=100 overflowing the 6-bit mask ([26]/[172]), the tail-sentinel comparison ([41]), VECTOR_SIM's Null-sorts-lowest semantics in the unindexed fallback ([48] [49]), external payloads masquerading as document start blocks ([44]), unvalidated metric/neighbour counts ([46] [47]), graph orphaning ([43]) and O(N^2) upsert ([45]). Existing tests only cover 2/3/6/8/32 dimensions plus one >1996 case, leaving the entire 307..1996 window - every real embedding size - untested.

5. Free-page reclamation is opportunistic rather than invariant-driven, so several paths silently orphan pages and grow the file without bound: [7] (safepoint destroys the commit-time link), [10] (DropCollection overwrites the chain), [40]/[78] (DropIndex never returns pages).

6. Lock and transaction release is written as sequential statements rather than try/finally, and the engine read lock is thread-affine while the release paths are not: [2]/[182], [4], [9], [133], [160]/[186], [162]. The consequence is always the same shape - a leaked read lock disables every checkpoint for the process lifetime and the log grows unbounded.

7. Rebuild/Recovery trusts a caller-supplied RebuildOptions with no fallback to engine settings and swallows loader failures. Verified: LiteDatabase.Rebuild(options=null) manufactures a null-password, null-collation option object, and FileReaderV8.HandleError rethrows only IOException. Group: [11]/[164] (canonical [11]), plus [13] [15] [16] [85] [165] [170] [171]. RebuildService also never copies the COLLATION pragma.

8. SharedEngine's cross-process mutex is acquired unconditionally, released conditionally, thread-affine, and never disposed: [157]/[180] (canonical [157]), [158]/[181] (canonical [158]), [159], [160]/[186] (canonical [160]), [161], [162], [163]. Any one of these leaves a Global mutex owned forever, which wedges the database file for every other process.

9. The hand-rolled SqlLike matcher is wrong in four independent ways plus its prefix helper; one rewrite fixes all of them. I confirmed each by re-implementing the function verbatim and sweeping all patterns of length <=5 over {a,b,%,_} against inputs of length <=4: 688 non-terminating cases and 136 negative-index cases ([173]), 92 false positives from the stale pattern char ([174]), 1364 false negatives from "%_" ([176]), and SqlLikeStartsWith("abc_") returning hasMore=false ([175]). Because StringResolver maps StartsWith/Contains/EndsWith onto LIKE ([150], which additionally passes user wildcards through unescaped), this is the highest-exposure correctness cluster in the codebase.

10. Unchecked narrowing and unvalidated framework-call arguments across the expression and engine API layers: [79]/[194], [100], [117]/[188], [118], [119], [121]/[190], [123]/[191]/[192], [124]/[193], [169], [171], [189]. The project sets no CheckForOverflowUnderflow, so every (int)/(byte) cast in this list wraps silently.

11. All three recursive-descent parsers (JSON, expression, tokenizer) lack a depth limit, and .NET makes StackOverflowException uncatchable, so untrusted text terminates the host process: [125]/[197] (canonical [125]), [111], [112]; [108] is the same hazard reached through a self-referencing document.

12. Untrusted on-disk bytes drive allocations, loop bounds and slice offsets with no validation, no cycle detection and no per-page checksum: [27] [28] [30] [34] [35] [38] [47] [86] [87] [88] [89] [90] [91] [92] [195]. Because PageFramePool allocates one array per 8- or 128-page segment, several of these read or write across page boundaries inside the shared buffer.

13. $system collections lazily enumerate other threads' live containers without the header lock that writers take: [80]/[95] (canonical [95]), [94], [96], and [93] for the disposed-snapshot variant.

14. Culture-sensitive ToUpper/ToLower/ToString on paths that must be invariant: [53] [131] [147] [148] [167] [178]. The codebase already uses ToUpperInvariant and an invariant NumberFormat elsewhere, so each of these is an inconsistency rather than a design choice.

Cross-tier note on severity: I raised [157]/[180] to critical (permanent cross-process wedge per the rubric) and [173] to critical (an unbounded loop inside an open read transaction wedges checkpoints and Dispose). I lowered [3] and [93] from "destroys the database" reasoning to high once AutoRebuild's false default was confirmed, and lowered the FileReaderV7 cluster ([87] [89] [90] [91] [195]) to medium/low because that reader is reachable only through Upgrade=true or a v4 file in RebuildService.

Cheapest high-leverage fixes, in order: (a) FlushToDisk on the log write path [12]; (b) seed RebuildOptions from _settings [11]/[164]; (c) rethrow non-IOException from FileReaderV8.Open or refuse to install an empty rebuild [85]; (d) rewrite SqlLike/SqlLikeStartsWith [173]-[176]; (e) reorder the operator reduction to respect equal precedence [110]; (f) add the `right != index.Tail` guard in IndexService.Find [39]/[41]; (g) wrap ReleaseMutex/ReleaseTransaction in try/finally and make the shared mutex acquisition symmetric [2] [157] [158].

## Coverage gaps

Judging by where the 203 findings cluster, the audit over-sampled expression/parser/mapper surface area and under-sampled the write path and concurrency.

Under-covered:
- Write path serialization. Every serializer finding is on a reader (BufferReader, BufferSliceExtensions.Read*, BsonElementReader). BufferWriter, WriteElement, WriteCString, the ObjectId/Guid/Decimal/DateTime encoders and BasePage.Update/Insert byte layout are essentially unexamined, yet finding [34] shows the write side is where a bad slice becomes on-disk corruption.
- WAL/checkpoint concurrency. Only [1] and [17] touch it, and [17] is self-admittedly unproven. There is nothing on RestoreIndex's version/position ordering, _currentReadVersion visibility, concurrent TryCheckpoint vs commit, or the disk writer queue's ordering guarantees - which is exactly the area the one confirmed critical durability bug ([12]) lives in.
- MemoryCache/PageFramePool internals. [23] and [24] are incidental; the reclaimer's eviction policy, ShareCounter transitions, segment growth under pressure and the Writable->Readable handoff are unaudited despite every page in the engine passing through them.
- Core DML semantics. No findings on Update/Upsert/DeleteMany correctness, multikey index maintenance (add/remove of the per-document node chain), or Include/DbRef resolution beyond [140] and [156]. IndexService.AddNode/DeleteSingleNode link maintenance is only reached indirectly via [37] and [39].
- SQL parser breadth. Coverage is REBUILD, LIMIT/OFFSET, the transaction verbs and keyword casing. UPDATE/INSERT/SELECT projection, aggregate and INTO parsing, and the JSON-document literal path, are untouched.
- Cross-process file sharing outside SharedEngine: FileStreamFactory's FileShare flags, byte-range locking, and what two direct-mode processes on one file actually do.
- Encryption beyond AesStream's constructor: the Write path, page-boundary alignment, and what happens when a password is added to an existing file.

Distribution artifacts to be aware of:
- Roughly 40 of the 203 entries are duplicates or facets (I marked 33 with duplicate_of), heavily concentrated in the crosscut-* areas, which were evidently run over ground the per-area passes had already covered. The true distinct-defect count is closer to 170.
- The vector module produced ~15 distinct defects, several of which make the feature unusable at real embedding sizes; that ratio suggests the module shipped with little adversarial review, and the remaining vector code (Search.cs, PruneNeighbors, level sampling) probably still hides more.
- Several findings are stated with explicit uncertainty ([8] [17] [26] [31] [41] [43] [141] [163]); of those I flagged six as shaky. Nothing in the set was verified by actually running the code - I could confirm SqlLike and the operator-precedence reduction only by re-implementing them outside the repo - so a maintainer should expect a reproduction step to be the first task on any item ranked below ~40.

## Excluded findings

### Refuted during verification

| Location | Claimed | Why it was rejected |
|---|---|---|
| `Engine/Disk/DiskService.cs:176` | medium | The quoted code and line are accurate: DiskService.cs:176 is `var stream = _writer.Value;` inside WriteLogDisk, _writer is `_logPool.Writer` (DiskService.cs:47), and StreamPool.cs:27 builds it as `new Lazy<Stream>(() => this.CreateWriter(appendOnly), true)`, i.e. ExecutionAndPublication, which does  |
| `Engine/Disk/MemoryCache.cs:485` | low | Code-reality check: the excerpt and line are accurate. /home/user/LiteDB/LiteDB/Engine/Disk/MemoryCache.cs:481-489 does call `_pool.EnsureIdleForDisposalLocked()` before `_disposed = true`, and /home/user/LiteDB/LiteDB/Engine/Disk/PageFramePool.cs:308-318 throws InvalidOperationException when any se |
| `Client/Shared/SharedEngine.cs:93` | medium | The excerpt and line number are accurate: /home/user/LiteDB/LiteDB/Client/Shared/SharedEngine.cs:91-106 is exactly as quoted (line 93 = `OpenDatabase();`, return value discarded), and `CloseDatabase()` (lines 75-87) disposes the engine whenever `!_transactionRunning && _engine != null`. But the find |

### Flagged as needing more evidence

These survived verification but the calibration pass judged them shaky. Do not act without confirming.

- **#10** [high] `Engine/Services/IndexService.cs:403` — Find's walk can advance past the tail sentinel for key types that sort above BsonType.MaxValue, returning "not found" for keys that exist
- **#36** [high] `Engine/Services/VectorIndexService.cs:197` — Insert drops the new node's forward links when the reverse prune rejects it, permanently orphaning the node (document silently missing from every vector search)
- **#77** [high] `Client/Mapper/TypeNameBinder/DefaultTypeNameBinder.cs:67` — _type deny-list is trivially bypassed: blocked gadget types can be reached as generic arguments, dictionary value types, or declared member types
- **#85** [medium] `Engine/Services/SnapShot.cs:219` — Snapshot local page cache is keyed only by pageID and cast unchecked, so a page first fetched as BasePage throws InvalidCastException when later fetched as DataPage/IndexPage
- **#141** [medium] `Client/Shared/SharedMutexNameFactory.cs:36` — Long database paths on Linux/macOS bypass the SHA-1 mutex-name fallback and fail mutex creation
- **#150** [medium] `Utils/Extensions/BufferSliceExtensions.cs:323` — WriteIndexKey's only key-size guard is DEBUG-only, and the length it records is an unchecked (ushort) truncation
- **#179** [low] `Engine/Services/WalIndexService.cs:76` — WalIndexService.Clear() resets transaction-ID and read-version bookkeeping before the log truncation that justifies it

### Not verified at all

13 of the 29 finder agents hit their 10-finding report cap, and 27 findings ranked below each finder's
top 8 were dropped before verification. They are not in this report. The audit is therefore a lower
bound on defect count, not an exhaustive list.
## Appendix — runnable evidence for the SqlLike defects

`SqlLike` (`LiteDB/Utils/Extensions/StringExtensions.cs:45-167`) was ported to Python verbatim,
modelling `collation.Compare(c, p) == 0` as ordinal equality (i.e. `Collation.Binary`), then
brute-forced against a reference SQL `LIKE` oracle. Harness: `docs/audit/sqllike_port.py`.

Sweep: patterns of length 1-5 over `{a, b, %, _}` against inputs of length 1-5 over `{a, b}`
— 84,568 cases.

| Outcome | Cases | Share |
|---|---|---|
| Non-terminating (infinite loop) | 2,122 | 2.5% |
| `IndexOutOfRangeException` | 330 | 0.4% |
| Wrong boolean result | 3,472 | 4.1% |

The wrong results break down as:

| Class | Cases | Example |
|---|---|---|
| False negative — a real match reported as no-match | 2,720 | `'a' LIKE '%_'` → `false` |
| False positive — pattern with wildcards over-matches | 700 | |
| False positive — wildcard-free pattern matches a longer string | 52 | `'aa' LIKE 'a'` → `true` |

Concrete cases, all reproducible with the harness:

```
'ab'    LIKE '%%a'   -> infinite loop (never returns)
'data'  LIKE '%%a'   -> infinite loop (never returns)
'ab'    LIKE '%%%a'  -> IndexOutOfRangeException
'Johnn' LIKE 'John'  -> true    (should be false)
'abccc' LIKE 'abc'   -> true    (should be false)
'a'     LIKE '%_'    -> false   (should be true)
'aa'    LIKE 'a%_'   -> false   (should be true)
```

### Why this is reachable from a search box

`LiteDB/Client/Mapper/Linq/TypeResolver/StringResolver.cs:34-36` maps LINQ string predicates onto
LIKE patterns by concatenation:

```csharp
case "StartsWith": return "# LIKE (@0 + '%')";
case "Contains":   return "# LIKE ('%' + @0 + '%')";
case "EndsWith":   return "# LIKE ('%' + @0)";
```

The parameter value is never escaped, so wildcard characters in application input become pattern
syntax. Chaining that with the defects above:

| LINQ call | Resulting pattern | Effect |
|---|---|---|
| `x.Name.EndsWith("%a")` | `%%a` | **query thread spins forever** inside an open read transaction |
| `x.Name.EndsWith("%%a")` | `%%%a` | `IndexOutOfRangeException` escapes the query pipeline |
| `x.Name.Contains("%")` | `%%%` | matches every document |
| `x.Name.Contains("_")` | `%_%` | matches every document of length ≥ 1 |

An unauthenticated user typing `%a` into a field bound to `EndsWith` hangs the thread, and because
the hang happens with the read transaction open, engine disposal blocks too.
