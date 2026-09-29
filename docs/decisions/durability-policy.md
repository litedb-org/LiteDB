# Durability policy (owner decisions)

Decided by the maintainer (@JKamsker) during the work on #3027, recorded on 2026-09-28. These
decisions govern how LiteDB handles storage that cannot sync, failed writes and the WAL. Code,
tests and docs are written against them; a change to a decision is made here first.

The work is delivered in layers (#3051, S09). This file records the decisions the current layer
implements and says which parts later layers add; see "Layer status" below.

## Scope (earlier decisions, still in force)

- Out of scope, best effort only: drive and memory defects, older LiteDB versions, and anyone
  else changing the database files while LiteDB runs.
- If LiteDB itself replaces or changes a file, it must handle that correctly.
- The same LiteDB version must "speak the same language" with itself, and that logic must not be
  able to go wrong.

## 1. WAL clearing (strict rule)

- The WAL, its header journal and any legacy header backup are removed only after a successful
  data sync covered every data write. Otherwise fail closed.
- The WAL may grow instead; if that isn't possible, fail when trying to write.
- Volatile logs (`:memory:`, `:temp:`, `LiteDatabase(Stream)` without a log) are exempt.

## 2. Read-only fallback

- If reading is possible, allow it.
- Writes throw a catchable `IOException` that doesn't corrupt anything, and the user can keep
  reading afterwards.
- The user must be able to find out without writing: `$database` diagnostics (`readOnly`,
  `readOnlyReason` and similar) report that the device is bad.

## 3. A write that cannot be persisted fails loudly

- With `durable commits` on (the default), a commit whose WAL can't be synced throws. It is never
  acknowledged as non-durable.
- The commit fails before any WAL frame is written: the log path is proven once per process to
  sync, before the first commit (note 4 covers a log that stops syncing later).

## 4. Durable in the WAL but not in the data file is allowed

- WAL syncs continue while only the data file can't sync, so commits stay durable.
- The WAL keeps growing and `$database` warns (`walKept`, WAL size, limit).
- The WAL limit is fixed but configurable: 1 GiB by default, set through the `wal limit`
  connection-string option and `EngineSettings.WalLimit`.
- Past the limit, writes throw and reads keep working. Writes resume once a data sync succeeds and
  a checkpoint drains the WAL.

## 5. `durable commits=false`: the user is on their own

- No log sync at commit. Don't crash the process and don't throw from an unrelated point, such as
  an automatic checkpoint or `Dispose`.
- Failures are reported through `$database`.

## 6. A real failure is sticky, in both modes

- Once a write or sync has actually failed (a non-durable one included), assume the device is bad
  and that later writes will fail too.
- Record the failure: file, operation, error, time, and whether the WAL was kept.
- Don't throw at the failure point unless it was the caller's own commit.
- The next write or commit, durable or not, throws right away before touching anything. The error
  carries details of the earlier failure.
- Reads keep working, and `$database` reports the recorded failure without a write.

## 7. Temporary code

- Before throwing temporary code away, put it somewhere findable: this repo or LiteDB-Artifacts.

## Proposed defaults (owner to confirm or change)

- **A.** "Cannot sync" (EINVAL/ENOTSUP, #2242) is not a failure when `durable commits=false`,
  because it is the reason to opt out. Real I/O errors, torn writes and similar count in both modes.
- **B.** Disk full counts as a sticky failure (the conservative choice); a reopen retries.
- **C.** The sticky state lasts until the database is reopened, not until a later write succeeds.
- **D.** Opt-out users still get the strict WAL rule (rule 1 protects the data file's integrity,
  not only recent commits).

## Second and third rounds (2026-09-28): the header frame

8. **The header is anchored in the WAL (refines 4).** Where only the data file cannot sync and
   the data header the WAL's frames depend on is not proven to be on the device (a database
   created there, a new process), a durable commit does not throw: the WAL holds a copy of that
   header, recovery takes the header from it when the device lost it, and the database stays
   writable with commits durable in the WAL.
10. **The anchor is a header frame in the WAL (form of 8).** The copy of the header is a WAL frame of
    its own at the start of the WAL: a checksummed frame of the header page that no transaction
    confirms, so recovery that does not look for it skips it; recovery that does takes the header,
    and the WAL salt that validates the following frames, from it when the data file's header is
    missing or invalid. It stays until a checkpoint whose data sync succeeded empties the WAL. Older
    6.x prereleases do not read it (older versions are out of scope).
11. **Every WAL starts with the header frame (replaces the condition in 8).** The first frame of every
    WAL generation (from empty until it is emptied again) is the header frame, in both commit modes,
    written by the first batch that extends an empty WAL and synced with that batch's commit. At open:
    - Data header intact, same salt: normal recovery.
    - Data header missing, empty or torn, and no header journal applies: recovery takes the header
      (and the salt that validates the frames) from the header frame.
    - Data header intact with another salt: a stale WAL generation, discarded as before (a salt
      changes only after a data sync covered the backfill).
    - Empty data file next to a log whose WAL holds frames: never initialized over. Restored from
      the header frame when it can be; otherwise the open fails loudly and changes neither file.
    Volatile logs have no header frame. A WAL without one (written before this change) opens as before.

## Second and third rounds (2026-09-28): durable commits, directory, limit and data barrier

9. **A WAL directory that cannot be synced or opened** (EACCES, EPERM, "cannot sync", #2242)
   fails a durable commit loudly, before it writes; `durable commits=false` commits there as
   5.0.21 did. Reading, recovery and consistency are unaffected: only a new WAL's file name is
   not provably durable, so a power loss could lose the whole WAL file.
12. **No unsafe mode.** On storage that never syncs, writes stop at the WAL limit (decision D
    stays). An explicitly named unsafe option that restores checkpoints flushed to the OS cache
    only (like SQLite's `synchronous=OFF`) may come later as a separate change.
14. **The data barrier before the first commit stays, best effort (refines 11).** The header frame
    covers what the WAL depends on (the data header), not what it builds on: a WAL holds changed
    pages only, and every other page must already be on the device. So before an engine's first
    durable commit LiteDB syncs the data file once, so that a database file someone copied into
    place (a backup restore, a deployment, a container image) is on the device before commits build
    on it. It never refuses a commit: a data file that cannot sync proceeds (the header frame covers
    the header), and a real I/O error is a recorded failure (decision 6). Cost: one data sync per
    process and data header in direct mode. Shared mode adds no work per operation: the barrier
    runs once per shared connection, and a connection that found the data file cannot sync does not
    retry it per operation. A copy restored while the process runs, with a header byte-identical to
    one it synced, is not synced again (best effort, documented).

Decision 13 (the read-only reopen after a failed commit shows only the commits acknowledged before
it) is a later layer's; see "Layer status".

## Layer status

The durability-accounting layer (S09a) implements:

- Rule 1 and default D: every data write is counted under the data writer's lock
  (`DiskService.DataWrites`), and the log is emptied or shrunk, a header journal or legacy header
  backup retired, and retired WAL slots cleared only behind a data sync that covered every write.
  Otherwise the operation stops before it removes anything ("stopped syncing").
- The overwrite barrier (note 12) in both commit modes.
- Rule 4 without the limit: while the data file cannot sync, checkpoints keep the WAL and retry the
  data sync first; `$database.walKept` reports it.
- Rule 6 recording: `$database.writeFailure` reports the record. At this layer the failure stops the
  engine for good (every later call on it throws the stop error, which carries the failure); an
  engine whose failure was recorded by a `$database` read refuses every write, sync and checkpoint
  and stops at its next write.
- Note 6 and default A: a refusal before anything was written is no failure.
- Note 13: the header write-back before a journal is retired.
- The failure boundary of sync helpers (note 17, #3052).
- The conservative slot-reuse baseline of note 15 (every fresh engine proved the data file and the
  log before its first slot reuse); the slot-reuse layer (S11) below drops it.

The header-frame layer (S09b) adds:

- Decisions 10 and 11: the header frame in every non-volatile checksummed WAL generation, its
  restore of a lost data header (plain and encrypted, note 9), the refusal to initialize a database
  over WAL frames, and the same restore in the rebuild's reader. A WAL without a header frame
  (written before this layer) opens as before.
- The header proof record (`DurableHeaders`): the data header each data file had at its latest
  successful sync in this process. The data proof before a slot reuse (note 15, until the S11
  layer drops it) skips the sync while the header still matches. The log proof record
  (`DurableLogs`) is forgotten when the log or its directory answers "cannot sync"; the commit
  proof that records it comes with rule 3.
- A failed header-frame write is truncated like a failed append; the commit fails and, at this
  layer, the engine stops.

The admission, limit and continuation layer (S09c) adds:

- Rule 3 (fail-loud durable commits, notes 2 and 4): before an engine's first WAL batch with durable
  commits, the log file and its directory are proven to sync, once per log path per process
  (`DurableLogs`); a log or directory that answers "cannot sync" fails the commit before it writes
  (`NotWritten`), and a log that stops syncing after the proof fails the commit whose sync found
  out with its outcome unknown (`OutcomeUnknown`). The recovery barriers (a checkpoint's journal, a
  conversion, a promotion) throw "The log file cannot sync" with durable commits instead of
  refusing quietly. "Cannot sync" stays distinct from a failed sync everywhere; opted-out commits
  keep degrading to an OS-cache flush (default A).
- The commit outcome (note 14): `Exception.Data["LiteDB.CommitOutcome"]` is `NotCommitted` only for
  a provable abort (refused before its first frame, failed before its confirmation with nothing torn,
  or its confirmation's failed append truncated away and that truncation synced), and `Unknown`
  otherwise.
- Decision 14 (the data barrier before the first commit, note 11).
- Rule 4's limit (note 8): `wal limit` (default 1 GiB), checked when a write starts; its syncs keep
  the failure boundary of note 17.
- Rule 2 and the rest of rule 6 (notes 5 and 7): a recorded failure stops the engine, whose next call
  reopens it read-only from the files as they are; reads keep working, every write throws
  `WriteFailedPrefix` with the record, and `$database` reports `readOnly`, `readOnlyReason`,
  `writeFailure`, `walKept`, `walLimit` and 64-bit file sizes. An open that must convert, migrate or
  repair the file where its storage cannot sync opens read-only instead of throwing (the open-time
  fallback), with the refusal as `readOnlyReason`.

The slot-reuse layer (S11) adds:

- Note 15: reusing a retired WAL slot needs no sync of its own. `ProveSlotReuse` goes; the
  allocation keeps its in-memory guards (storage known not to sync, and a log whose syncs cannot
  report failure, never reuse slots). A shared-mode operation that reuses slots syncs the log once
  for its commit and the data file not at all (was: one more log sync, and a data sync unless the
  header was one this process synced).

Still later: decision 13 (the reopen replays the WAL only up to the last commit acknowledged before
a failed batch; until then the reopen replays the files as they are, so a commit whose outcome is
unknown may show), a shared connection keeping a failure for its later operations (until then each
operation opens a fresh engine, which proves the log again), and the reopen lifecycle under
concurrent callers (waiting for the failed engine's teardown, repeated disposal).

## Implementation notes (how the code applies the decisions)

2. **The WAL's directory (decision 9).** A new WAL's name is durable only after a directory sync. A
   directory that answers "cannot sync" or cannot be opened (EACCES, EPERM) fails a durable commit
   like a log that cannot sync. 5.0.21 never synced directories; `durable commits=false` restores that.

3. **Syncs that cannot report failure (an existing exception, stated explicitly).** Without a C
   library on Unix the runtime's `Flush(true)` loses errors: syncs are attempted, commits are
   acknowledged, `durableLogFlush` is false, and a checkpoint's barrier counts such a sync as
   synced. It is not a known failure, so nothing is recorded; such a log never reuses WAL slots.
   A durable-commit open logs it once.
4. **A log that stops syncing after the proof.** The proof runs once per log path per process, before
   the first commit. A later commit whose own sync answers "cannot sync" has already written its frames:
   it throws, and its error says its outcome is unknown (the frames reached the operating system).
5. **Sticky failure mechanics (6).** The failing operation stops the engine as before; once the
   failure's teardown closed the services, the next call reopens it read-only from the files as they
   are. A call that arrives before that teardown finished gets the stop error; calls that arrive during
   the reopen wait for it. An explicit transaction the failure ended throws at `Commit` or at the next
   `BeginTrans` on its thread, whichever comes first (and `Rollback` returns true). An explicit
   `Checkpoint()` is the caller's own operation and throws, also after the reopen; an automatic
   checkpoint after a successful commit and the one in `Dispose` do not. A sync that fails where no
   write can throw it (a `$database` read) is recorded and reported there, and the next call stops the
   engine. An in-memory or temporary database stays closed (its streams went with the failed engine).
   A read-only engine never auto-rebuilds and never writes the invalid-state mark.
6. **What counts as a failure (A, D).** A data sync that answers "cannot sync" before a checkpoint
   or promotion writes is not a failure: the checkpoint writes nothing and the WAL is kept, a
   promotion is refused with both files unchanged. After it wrote, it is a failure in both modes:
   the engine's state (for example a rotated WAL salt) no longer matches the files on the device, so
   no write may follow in that engine. "Cannot sync" and a failed sync (an I/O error) stay distinct
   everywhere: the first degrades or refuses, the second is recorded and stops.
7. **Diagnostics.** `$database.writeFailure` is `{file, operation, error, time, walKept}` or null;
   after the reopen `readOnly` is true and `readOnlyReason` is the failure (or, after the open-time
   fallback, the refusal). `walKept`, `logFileSize` and `walLimit` (all 64-bit) show how far a kept WAL
   is from the limit. The record's `walKept` counts whatever the log file holds (an outstanding header
   journal too); `durableLogFlush` is false after a failure on the log.
8. **The WAL limit** is checked when a write starts: a transaction already running may commit past it.
   Each refused write first retries the log sync and the data sync, so writes resume on their own once
   the storage syncs; the next checkpoint then drains the WAL. Those syncs go through the helpers of
   note 17: they recheck under the lock that orders them and record a real I/O failure before they
   release it; the limit check then stops the engine.
11. **The data barrier (14)** runs before an engine's first durable commit, skipped when the data file
   is not a file, when this engine already synced it, and, in a shared connection, after the
   connection's first barrier or once it found the data file cannot sync. A real I/O error fails that
   commit and is recorded (decision 6).
12. **No overwrite behind a recovery copy that is only in the OS cache (1, D).** A checkpoint's
   backfill, a format promotion, a conversion and the invalid-state mark overwrite the data file
   behind its header journal and WAL. When the log's latest barrier answered "cannot sync", they
   refuse before the journal is written, in both commit modes: a checkpoint writes nothing and keeps
   the WAL (quietly), a compact write skips the v12 promotion and stores BSON, and a conversion or
   any other promotion is refused with both files unchanged. Refused before it wrote, none of these
   is a failure (note 6). The log stopping to sync after the journal was written is one. With
   durable commits the barrier itself throws "The log file cannot sync" (decision 3): an explicit
   checkpoint throws it, and an open that must convert or promote the file opens read-only with it
   as its reason (note 5). A checkpoint also writes only to a data file that just synced. The log
   counts as unsyncable only while its latest sync answered so; each checkpoint retries one sync
   first. The WAL limit then holds, as no checkpoint can drain the WAL; a write past it tries a log
   sync first, as it tries the data sync (note 8), and `$database.walKept` reports the kept WAL.
13. **A sync that failed with an I/O error ("fsyncgate").** Linux marks the pages it could not write
   back clean: they stay in the page cache, and a later sync writes nothing. Recovery never takes a
   header it reads as durable on that ground: before the sync that retires a header journal, it
   makes the journal durable and writes the header back as it read it. The engine never retries a
   sync on a handle whose sync failed (decision 6).
14. **The outcome of a failed commit** is in `Exception.Data["LiteDB.CommitOutcome"]`, whatever the
   exception type: `"NotCommitted"` when its confirmation cannot be in the log (refused before its first
   frame, failed before its confirmation's write with no frame of it left torn, or its confirmation's
   failed append truncated away and the truncation synced: a confirmation can reach the device whole
   although its write threw, and a truncation is not durable before a sync, so the engine syncs the log
   on that failure path, in both commit modes; a sync that cannot report failure proves nothing),
   `"Unknown"` when it may be (a write that may have left a frame behind: a torn overwrite, a frame a
   buffering stream still held, a failed truncation; a confirmation whose truncation could not be
   synced, or that was written before a later step failed; a failed sync after the confirmation). After
   `"Unknown"` a reopen may show the commit: retries need idempotent writes, or a check after reopening.
   Decision 3's "fails before any frame is written" holds for a log known not to sync (the proof before
   an engine's first commit); a log that stops syncing later fails the commit with its outcome unknown
   (note 4).
9. **What the header frame restores (11).** Only a header a power loss left unwritten: the data file
   is empty or shorter than a page, or every 512-byte sector of its header is the header frame's or
   was never written (zeros; in an encrypted file, zero ciphertext, which decrypts to one fixed block
   in AES ECB mode). A header with other bytes (a drive defect, another database's or an older
   generation's sector) fails the open as before, instead of taking a copy that may be older. The
   header frame must not name pages the data file lost (its `LastPageID` must fit the file); an empty
   data file beside a WAL whose header frame names pages, a missing data file beside a WAL, and a log
   whose header frame cannot be read (torn, never written back, or encrypted and opened without its
   password) while it holds WAL frames refuse the open and change neither file. A wrong password
   is refused before anything is written. A header journal wins where one applies. **A header frame
   is not a backup of every historical data page:** it restores the header only, and only while the
   data file still holds every page that header names. A data page the device lost is not recovered
   from it; the supported lost-page fault model is unchanged.
15. **Reusing a retired WAL slot needs no proof of its own.** A retiring checkpoint syncs its witness
   records before the root that names them, syncs the root before its journal goes, and a pending journal
   makes the next open sync the data file or open read-only; a clear that did not reach the device leaves
   a witnessed slot, which recovery skips. So reuse costs no sync per engine or shared operation; storage
   known not to sync still never reuses slots, nor does a log whose syncs cannot report failure (note 3).

   Why that ordering suffices without a sync before the first reuse (the proof it replaces, dev's
   `ProveLogSync` and later `ProveSlotReuse`, synced the raw log, and the data file once per data
   header, in every fresh engine, so in every shared-mode operation that reused a slot):
   - An engine takes a free slot from two places only: at open, the slots the root named by the data
     header witnesses and no live version occupies (`RestoreIndex`; a writable open keeps only a
     checksummed WAL), and the slots its own checkpoint cleared, published only after the clears'
     log sync succeeded (`ReclaimLogPages`, which clears only witnessed slots).
   - That root is durable before any engine can reuse its slots. The retiring checkpoint syncs the
     witness records before it writes the root (`PrepareRetirement`), and syncs the root before the
     header journal is retired; a root whose data sync did not succeed throws and keeps the journal
     (`CompletePartialCheckpoint`). An open that finds that journal writes the header back and syncs
     it before retiring the journal (note 13), so a root a failed sync left in the cache only (an
     EIO that marked the page clean) reaches the device, or the open is read-only and reuses nothing.
     The proof's data sync repeated a sync that had already succeeded.
   - A reused slot's contents after a power loss are one of: its old frame (the clear never reached
     the device), a torn or unconfirmed new frame, or the new frame of a commit whose own log sync
     made it durable (confirmations always append, and the commit's sync covers every frame before
     it). Recovery skips the old frame because the durable root witnesses it, skips a torn or
     unconfirmed frame as always, and replays the confirmed one. The proof's log sync made durable
     clears that recovery does not need.
   - Readers are unaffected: a slot is free only behind the reclamation fence, which excludes every
     accepted snapshot that could reach the old frame, whatever syncs.
   - The proof also found out, before the first reuse, that storage cannot sync. That is now the
     commit's: with durable commits the commit's own log proof (rule 3) refuses it before it writes;
     opted out, an engine that never learned it reuses witnessed slots, its commits claim no
     durability, and a power loss keeps exactly the commits synced before.
   The proof was therefore redundant with this ordering, not a missing flush. Weakening any link
   above (publishing a slot before its root is durable, retiring a journal behind a failed root sync,
   or skipping the header write-back) needs the proof back. Evidence: `SlotReuseWithoutProof_Tests`,
   `MvccUnsyncableLog_Tests`, `SharedUnsyncableLog_Tests` and `RetiredSlotPowerLoss_Tests`, with
   power-loss images at every WAL write of a reusing commit; `FreshEngineDurability_Tests` and
   `CheckpointDataSync_Tests` count the syncs.
17. **The failure boundary of sync helpers (#3052).** A helper that issues a sync after an earlier
   admission check (`LogSyncs`, `DataFileSyncs`, `ProveRetirementSyncs`, `SyncLogBeforeCheckpoint`,
   a checkpoint once it holds the WAL writer, a WAL batch once it holds it) rechecks the stopped and
   recorded-failure state after it acquired the lock that orders the sync. A real I/O failure of a
   helper's own sync is recorded before that lock is released, so every waiter's recheck refuses;
   teardown, when the caller stops the engine, follows outside the lock. The first failure recorded
   stays the one reported. "Cannot sync" is never published this way. A `$database` read whose sync
   failed reports the record instead of throwing, and the engine's stop is due.
