# Durability policy (owner decisions)

Decided by the maintainer (@JKamsker) during the work on #3027, recorded on 2026-09-28. These
decisions govern how LiteDB handles storage that cannot sync, failed writes and the WAL. Code,
tests and docs are written against them; a change to a decision is made here first.

Implementation status is tracked in the description of #3027 ("Safety / regression evidence").
Copies: the PR description, and `pull-requests/3027-io-regressions-since-5021/decisions.md` in
[LiteDB-Artifacts](https://github.com/litedb-org/litedb-artifacts).

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
- The commit fails before any WAL frame is written. The planned mechanism: prove once per log path
  per process that the log syncs, before the first commit.

## 4. Durable in the WAL but not in the data file is allowed

- WAL syncs continue while only the data file can't sync, so commits stay durable. The rule that
  made log syncs wait for the data file is dropped.
- The WAL keeps growing and `$database` warns (`walKept`, WAL size, limit).
- The WAL limit is fixed but configurable: 1 GiB by default, set through a new `wal limit`
  connection-string option and `EngineSettings`.
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
  because it is the reason to opt out. It counts as a failure only with durable commits on. Real
  I/O errors, torn writes and similar count in both modes.
- **B.** Disk full counts as a sticky failure (the conservative choice); a reopen retries.
- **C.** The sticky state lasts until the database is reopened, not until a later write succeeds.
  For shared connections it is kept connection-wide, because each operation opens a fresh engine.
- **D.** Opt-out users still get the strict WAL rule and the WAL limit (rule 1 protects the data
  file's integrity, not only recent commits). This one was an open question.

## Second round (2026-09-28)

8. **The header is anchored in the WAL (refines 4).** Where only the data file cannot sync and
   the data header the WAL's frames depend on is not proven to be on the device (a database
   created there, a new process), a durable commit does not throw. Before the first such commit,
   the engine writes a copy of that header into the log (the header journal) and syncs the log.
   Recovery takes the header from that copy when the device lost it, keeps the copy until a data
   sync succeeds, and stays writable, with commits durable in the WAL; the WAL keeps growing up to
   the WAL limit. A leftover log with such a copy next to an empty data file restores its
   database: that only happens when someone else deletes files, which is out of scope.
9. **A WAL directory that cannot be synced or opened** (EACCES, EPERM, "cannot sync", #2242)
   fails a durable commit loudly, before it writes; `durable commits=false` commits there as
   5.0.21 did. Reading, recovery and consistency are unaffected: only a new WAL's file name is
   not provably durable, so a power loss could lose the whole WAL file.
10. **The anchor is a header frame in the WAL (form of 8).** The existing header journal is a
    temporary footer that cannot stay while commits append behind it, so the copy of the header is
    a WAL frame of its own at the start of the WAL (chosen over a separate file next to the
    database). It is a checksummed frame of the header page that no transaction confirms, so
    recovery that does not look for it skips it; recovery that does takes the header, and the WAL
    salt that validates the following frames, from it when the data file's header is missing or
    invalid. It stays until a checkpoint whose data sync succeeded empties the WAL. Older 6.x
    prereleases do not read it (older versions are out of scope).

## Third round (2026-09-28, after an independent second opinion)

The owner asked for an independent review that trusted none of the earlier decisions. Its findings
and the owner's answers:

11. **Every WAL starts with the header frame (replaces the condition in 8).** The WAL describes
    itself, as SQLite's WAL header does: the first frame of every WAL generation (from empty until
    it is emptied again) is the header frame of decision 10, in both modes, written by the first
    batch that extends an empty WAL and synced with that batch's commit. Not only when the data
    header is unproven. At open:
    - Data header intact, same salt: normal recovery.
    - Data header missing, empty or torn, and no header journal applies: recovery takes the header
      (and the salt that validates the frames) from the header frame.
    - Data header intact with another salt: a stale WAL generation, discarded as before (a salt
      changes only after a data sync covered the backfill).
    - Empty data file next to a log whose WAL holds frames: never initialized over. Restored from
      the header frame when it can be; otherwise the open fails loudly and changes neither file.
      Before this, such an open created a new database and silently discarded every frame (for
      example `durable commits=false` on storage that cannot sync, then a power loss).
    Commits no longer depend on the data header being on the device, so the "header not proven"
    refusal and its per-process proof go away for commits. Volatile logs have no header frame.
12. **No unsafe mode in this PR.** On storage that never syncs, writes stop at the WAL limit
    (decision D stays). An explicitly named unsafe option that restores checkpoints flushed to the
    OS cache only (like SQLite's `synchronous=OFF`) may come later as a separate change.
13. **The read-only reopen shows only acknowledged commits.** When a commit fails after its frames
    reached the operating system ("outcome unknown"), the read-only engine that replaces the failed
    one replays the WAL only up to the last commit acknowledged before the failure, so this process
    never sees a transaction its caller saw fail. A later open (a new connection, a restart) lets
    the device decide, as recovery always does. If the WAL grew past the failed batch meanwhile
    (another process committed on top of it), the files win.
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


## Implementation notes (how the code applies the decisions; owner to confirm)

These are the implementer's readings where the decisions leave a detail open. They are not decisions
until the owner confirms them.

1. **Header rule (refines 4; superseded by decisions 8 and 11: every WAL starts with a copy of the header).** WAL frames depend on the data header they were written under (its WAL
   salt, file version and creation time). A commit durable only in the WAL is durable only while that
   header is on the device: the engine synced it, or it is byte-identical to the header a successful
   sync in this process left (the process-wide durable-header record). While the data file cannot sync
   and the header is not proven (a database created on such storage, or a new process), a commit with
   durable commits throws before it writes (decision 3). So "durable in the WAL, not in the data file"
   holds once the header was proven, typically when the data file stops syncing while the process runs.
2. **The WAL's directory (confirmed by decision 9).** A new WAL's name is durable only after a directory sync. A directory that
   answers "cannot sync" or cannot be opened (EACCES, EPERM) fails a durable commit like a log that
   cannot sync. 5.0.21 never synced directories; `durable commits=false` restores that.
3. **Syncs that cannot report failure.** Without a C library on Unix the runtime's `Flush(true)` loses
   errors: syncs are attempted, commits are acknowledged, `durableLogFlush` is false. Not a known
   failure, so nothing is recorded.
4. **A log that stops syncing after the proof.** The proof runs once per log path per process, before
   the first commit. A later commit whose own sync answers "cannot sync" has already written its frames:
   it throws, and its error says its outcome is unknown (the frames reached the operating system).
5. **Sticky failure mechanics (6).** The failing operation stops the engine as before; the next call
   reopens it read-only from the files as they are (after a failed WAL batch, only up to the last
   commit acknowledged before it, decision 13). Operations running on other threads at that moment
   fail with the stop error; calls that arrive during the reopen wait for it. An explicit transaction the
   failure ended throws at `Commit` or at the next `BeginTrans` on its thread, whichever comes first (and
   `Rollback` returns true). An explicit `Checkpoint()` is the caller's own operation and throws, also
   after the reopen; an automatic checkpoint after a successful commit and the one in `Dispose` do not. A
   data sync that fails where no write can throw it (a `$database` read) is recorded and reported there,
   and the next call stops the engine. A shared connection keeps the failure: its later operations open
   read-only until the connection is reopened (per connection: two shared connections do not share it).
   A read-only engine never auto-rebuilds.
6. **What counts as a failure (A, D).** A data sync that answers "cannot sync" before a checkpoint or
   promotion writes is not a failure: the checkpoint writes nothing and the WAL is kept. After it wrote,
   it is a failure in both modes: the engine's state (for example a rotated WAL salt) no longer matches
   the files on the device, so no write may follow in that engine. Read failures and damaged files stop
   the engine as before, without a read-only reopen.
7. **Diagnostics.** `$database.writeFailure` is `{file, operation, error, time, walKept}` or null;
   after the reopen `readOnly` is true and `readOnlyReason` is the failure. `walKept`, `logFileSize` and
   `walLimit` show how far a kept WAL is from the limit. The record's `walKept` counts whatever the log
   file holds (an outstanding header journal too); `durableLogFlush` is false after a failure on the log.
8. **The WAL limit** is checked when a write starts: a transaction already running may commit past it.
   Each refused write first retries the data sync, so writes resume on their own once the data file
   syncs; the next checkpoint then drains the WAL.
9. **What the header frame restores (11).** Only a header a power loss left unwritten: the data file is
   empty or shorter than a page, or every 512-byte sector of its header is the header frame's or zeros.
   A header with other bytes (a drive defect) fails the open as before, instead of taking a copy that
   may be older. The header frame must not name pages the data file lost (its `LastPageID` must fit the
   file); an empty data file beside a WAL whose header frame names pages, and a missing data file
   beside a WAL, refuse the open and change neither file, and so does a log whose header frame cannot be
   read (torn, never written back, or encrypted and opened without its password) while it holds WAL
   frames. An encrypted data file whose header page was never written back, or only some of its sectors
   (a page write spans 16 sectors and is not atomic), is restored like a plain one: a sector never
   written holds zero ciphertext, which decrypts to one fixed block (AES in ECB mode), and a sector of
   that block alone counts as never written; any other bytes still fail the open unchanged. An empty
   encrypted data file is restored too. A header frame whose write fails is truncated like a failed
   append.
10. **Which reopens are bounded (13).** A failed WAL write and a failed commit log flush record the end of
   the WAL before their batch and the files as the failure left them: the raw log from that end on,
   its salt, the data file's length and header, and its copy of every page the failed transaction
   wrote. The read-only engine that replaces the failed one, and a shared connection's later read-only
   engines (its read snapshots included), replay up to that end only while all of these still match:
   another connection or process can recover the failed commit and checkpoint it into the data file
   without changing the log's length or salt, and a view bounded then would mix the two. Otherwise the
   files win, whole. Other failures (a checkpoint, a promotion) wrote no commit and are not bounded.
11. **The data barrier (14)** runs before an engine's first durable commit, skipped when the data file
   is not a file, when this engine already synced it, and, in a shared connection, after the
   connection's first barrier or once it found the data file cannot sync. A real I/O error fails that
   commit and is recorded (decision 6).
12. **No overwrite behind a recovery copy that is only in the OS cache (1, D; external review point 1).**
   A checkpoint's backfill, a format promotion, a conversion and the invalid-state mark overwrite the data
   file behind its header journal and WAL. When the log answers "cannot sync", they refuse before the
   journal is written, also with `durable commits=false`: a checkpoint writes nothing and keeps the WAL
   (quietly, decision 5), a compact write skips the v12 promotion and stores BSON (as while the data file
   cannot sync), a conversion opens read-only, and any other promotion is refused with both files
   unchanged. Refused before it wrote, none of these is a failure (note 6): nothing is recorded and the
   engine keeps writing. The log stopping to sync after the journal was written is one. `durable
   commits=false` gives up recent commits, never the data file's integrity (SQLite's `synchronous=NORMAL`
   keeps its checkpoint barriers too). The WAL limit then holds, as no checkpoint can drain the WAL; a
   write past it tries a log sync first, as it tries the data sync (note 8), and `$database.walKept`
   reports the kept WAL. The log counts as unsyncable only while its latest sync answered so. A WAL
   directory that cannot sync does not block a checkpoint: once the file's own sync succeeded, its name
   is durable in practice (ext4, xfs, btrfs), though POSIX does not promise it, so a durable commit still
   refuses there (decision 9).
13. **A sync that failed with an I/O error ("fsyncgate").** Linux marks the pages it could not write back
   clean: they stay in the page cache, and a later sync writes nothing. Recovery never takes a header it
   reads as durable on that ground: before the sync that retires a header journal, it makes the journal
   durable and writes the header back as it read it. WAL pages are copied again by the first checkpoint of
   every reopen, and the engine never retries a sync on a handle whose sync failed (decision 6).
14. **The outcome of a failed commit** is in `Exception.Data["LiteDB.CommitOutcome"]`: `"NotCommitted"` when
   no frame of it can be in the log (refused before its first frame, or a failed append truncated away),
   `"Unknown"` when frames may have reached the log (a write that threw, a failed sync after the
   confirmation). After `"Unknown"` a reopen may show the commit: retries need idempotent writes, or a check
   after reopening. Decision 3's "fails before any frame is written" holds for a log known not to sync
   (the proof before an engine's first commit); a log that stops syncing later fails the commit with its
   outcome unknown (note 4).
15. **Reusing a retired WAL slot needs no proof of its own.** A retiring checkpoint syncs its witness
   records before the root that names them, syncs the root before its journal goes, and a pending journal
   makes the next open sync the data file or open read-only; a clear that did not reach the device leaves
   a witnessed slot, which recovery skips. So reuse costs no sync per engine or shared operation; storage
   known not to sync still never reuses slots, nor does a log whose syncs cannot report failure (note 3).
16. **Power-safe overwrite is assumed** (see `docs/header-publication.md`, "Encoding and durability
   limits", and the fault model in `docs/storage-stack-safety.md`): a 8,256-byte frame shares sectors
   with its neighbours, and a write that tears a sector must not damage the bytes of that sector it did
   not write. Checksums detect such damage; they cannot repair it.
