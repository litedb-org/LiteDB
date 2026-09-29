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
- The user must be able to find out without writing: `$database` diagnostics report that the
  device is bad.

## 3. A write that cannot be persisted fails loudly

- With `durable commits` on (the default), a commit whose WAL can't be synced throws. It is never
  acknowledged as non-durable.

## 4. Durable in the WAL but not in the data file is allowed

- WAL syncs continue while only the data file can't sync, so commits stay durable.
- The WAL keeps growing and `$database` warns (`walKept`, WAL size, limit).
- The WAL limit is fixed but configurable (1 GiB by default, `wal limit`). Past the limit, writes
  throw and reads keep working.

## 5. `durable commits=false`: the user is on their own

- No log sync at commit. Don't crash the process and don't throw from an unrelated point, such as
  an automatic checkpoint or `Dispose`.
- Failures are reported through `$database`.

## 6. A real failure is sticky, in both modes

- Once a write or sync has actually failed, assume the device is bad and that later writes will
  fail too.
- Record the failure: file, operation, error, time, and whether the WAL was kept.
- The next write or commit throws right away before touching anything. The error carries details
  of the earlier failure.

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
- The conservative slot-reuse baseline (note 15).

The header-frame layer (S09b) adds:

- Decisions 10 and 11: the header frame in every non-volatile checksummed WAL generation, its
  restore of a lost data header (plain and encrypted, note 9), the refusal to initialize a database
  over WAL frames, and the same restore in the rebuild's reader. A WAL without a header frame
  (written before this layer) opens as before.
- The header proof record (`DurableHeaders`): the data header each data file had at its latest
  successful sync in this process. The data proof before a slot reuse (note 15) skips the sync while
  the header still matches. The log proof record (`DurableLogs`) is forgotten when the log or its
  directory answers "cannot sync"; the commit proof that records it comes with rule 3.
- A failed header-frame write is truncated like a failed append; the commit fails and, at this
  layer, the engine stops.

Later layers add: fail-loud durable commits (rule 3), the WAL limit, the commit outcome, the data
barrier before the first commit, the read-only open fallback and read-only continuation (rule 2 and
the rest of rule 6) (S09c). Until then a durable
commit on a log that answers "cannot sync" degrades to an OS-cache flush as before (with
`durableLogFlush` false), and an open that must convert or promote the file on such storage throws
the refusal instead of opening read-only.

## Implementation notes (how the code applies the decisions)

3. **Syncs that cannot report failure (an existing exception, stated explicitly).** Without a C
   library on Unix the runtime's `Flush(true)` loses errors: syncs are attempted, commits are
   acknowledged, `durableLogFlush` is false, and a checkpoint's barrier counts such a sync as
   synced. It is not a known failure, so nothing is recorded; such a log never reuses WAL slots.
6. **What counts as a failure (A, D).** A data sync that answers "cannot sync" before a checkpoint
   or promotion writes is not a failure: the checkpoint writes nothing and the WAL is kept, a
   promotion is refused with both files unchanged. After it wrote, it is a failure in both modes:
   the engine's state (for example a rotated WAL salt) no longer matches the files on the device, so
   no write may follow in that engine. "Cannot sync" and a failed sync (an I/O error) stay distinct
   everywhere: the first degrades or refuses, the second is recorded and stops.
12. **No overwrite behind a recovery copy that is only in the OS cache (1, D).** A checkpoint's
   backfill, a format promotion, a conversion and the invalid-state mark overwrite the data file
   behind its header journal and WAL. When the log's latest barrier answered "cannot sync", they
   refuse before the journal is written, in both commit modes: a checkpoint writes nothing and keeps
   the WAL (quietly), a compact write skips the v12 promotion and stores BSON, and a conversion or
   any other promotion is refused with both files unchanged. Refused before it wrote, none of these
   is a failure (note 6). The log stopping to sync after the journal was written is one. A
   checkpoint also writes only to a data file that just synced. The log counts as unsyncable only
   while its latest sync answered so; each checkpoint retries one sync first.
13. **A sync that failed with an I/O error ("fsyncgate").** Linux marks the pages it could not write
   back clean: they stay in the page cache, and a later sync writes nothing. Recovery never takes a
   header it reads as durable on that ground: before the sync that retires a header journal, it
   makes the journal durable and writes the header back as it read it. The engine never retries a
   sync on a handle whose sync failed (decision 6).
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
15. **Slot reuse, conservative baseline.** Before its first reuse of a retired WAL slot, every
   engine proves that the data file and the log sync (`DiskService.ProveSlotReuse`); storage known
   not to sync, and a log whose syncs cannot report failure, never reuse slots. For a data file, the
   data sync is skipped while its header is one a successful sync in this process left
   (`DurableHeaders`; a copy restored over the file with a byte-identical header is taken as proven,
   best effort). Every fresh engine (every shared-mode operation) pays that proof once; dropping it
   behind the durable witness root is a separate, later change.
17. **The failure boundary of sync helpers (#3052).** A helper that issues a sync after an earlier
   admission check (`LogSyncs`, `DataFileSyncs`, `ProveRetirementSyncs`, `SyncLogBeforeCheckpoint`,
   a checkpoint once it holds the WAL writer, a WAL batch once it holds it) rechecks the stopped and
   recorded-failure state after it acquired the lock that orders the sync. A real I/O failure of a
   helper's own sync is recorded before that lock is released, so every waiter's recheck refuses;
   teardown, when the caller stops the engine, follows outside the lock. The first failure recorded
   stays the one reported. "Cannot sync" is never published this way. A `$database` read whose sync
   failed reports the record instead of throwing, and the engine's stop is due.
