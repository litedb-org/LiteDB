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

1. **Header rule (refines 4; superseded by decision 8, which anchors the header in the WAL).** WAL frames depend on the data header they were written under (its WAL
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
   reopens it read-only from the files as they are. Operations running on other threads at that moment
   fail with the stop error. An explicit transaction the failure ended throws at `Commit` (and `Rollback`
   returns true). An explicit `Checkpoint()` is the caller's own operation and throws; an automatic
   checkpoint after a successful commit and the one in `Dispose` do not. A shared connection keeps the
   failure: its later operations open read-only until the connection is reopened.
6. **What counts as a failure (A, D).** A data sync that answers "cannot sync" before a checkpoint or
   promotion writes is not a failure: the checkpoint writes nothing and the WAL is kept. After it wrote,
   it is a failure in both modes: the engine's state (for example a rotated WAL salt) no longer matches
   the files on the device, so no write may follow in that engine. Read failures and damaged files stop
   the engine as before, without a read-only reopen.
7. **Diagnostics.** `$database.writeFailure` is `{file, operation, error, time, walKept}` or null;
   after the reopen `readOnly` is true and `readOnlyReason` is the failure. `walKept`, `logFileSize` and
   `walLimit` show how far a kept WAL is from the limit.
8. **The WAL limit** is checked when a write starts: a transaction already running may commit past it.
   Each refused write first retries the data sync, so writes resume on their own once the data file
   syncs; the next checkpoint then drains the WAL.
