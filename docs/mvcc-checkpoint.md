# Snapshot-aware checkpointing

An ordinary query pins its logical read version for its entire lifetime, including
pages it has not visited. Checkpoint backfills the newest committed image of each
page at or below the oldest live snapshot. It does not wait for readers to finish.
`Checkpoint()` returns the number of pages copied; with a version-zero reader it
can return zero. Dispose streaming readers promptly to permit WAL reclamation.

## Backfill and reclamation

This implementation keeps one append-only committed WAL generation. It separates
copying safe page images into the data file from reclaiming that generation:

* Local snapshot capture/registration, commit-index publication, and checkpoint
  boundary selection use the WAL index lock. Every collection snapshot registers,
  including write snapshots and snapshots opened later in a transaction.
* The backfill boundary is the minimum of the current commit version, all local
  snapshot versions, and all live shared-reader versions.
* Only the latest committed frame for each page at that boundary is copied.
  Header frames participate in the same version index. Safepoint frames from an
  uncommitted transaction cannot be selected.
* Committed frames are never moved, overwritten, or removed while readers exist.
  Transaction-private unconfirmed slots can still be reused by their owner.
* Full reset requires both exclusive local transaction ownership and no shared
  reader leases. Flush the WAL, backfill and durably flush data, then durably
  truncate the WAL. Only then reset the version counters and index.

For a snapshot S, a page with a WAL version at or below S resolves to an immutable
WAL offset. If there is no such version, no backfill at or below S can change that
page, so its original data-file image remains valid. This covers both an untouched
page and a reader suspended between resolving an offset and reading its bytes.

### Why no persistent base field is needed

The complete WAL, including already-backfilled frames and confirmation records,
is retained until full reset. Recovery can reconstruct the same version numbering
by replaying that WAL; page resolution continues to prefer the WAL even for frames
already copied to data. The in-memory backfill version only avoids duplicate
writes in one engine instance. Reopening may safely repeat the copies.

Consequently there is no separately published persistent base, prefix deletion,
or generation switch to recover. Existing v8/v9 page formats remain unchanged.
This deliberately trades reclamation granularity for a smaller recovery protocol:
a long-lived reader can still grow the WAL and its index, even though it no longer
prevents backfill or commits. Incremental physical reclamation would require an
additional generation/epoch protocol and is not performed here.

## Shared mode

Ordinary `SharedEngine.Query` uses a private read-only engine with a fixed WAL
index and an OS-held lease in `<database filename>-readers/`. Under the existing
named database mutex it opens/replays the database and publishes a lease whose
name contains the captured version. It then releases the mutex before returning
the reader. The engine and lease remain alive until reader disposal. Temporary
sort storage is private to each query, including when a sort spills to disk.

Writers continue to own the mutex through their operation and commit. Every
checkpointer inspects shared leases while owning that same mutex. The reader's
engine closes before releasing its lease, so neither streams nor cached WAL
offsets outlive their protection. A newly opened query replays newer commits.

An exclusively opened lease file is the liveness primitive. The process keeps its
handle open; another participant can only open it exclusively after the OS has
released that handle. Stale files are then deleted under the database mutex.
No PID reuse or heartbeat timeout can evict a paused but live reader. Interrupted
registration is safe because replay, registration, and checkpoint are ordered by
the same mutex. After a machine restart there are no surviving reader handles.

Explicit shared transactions, `FOR UPDATE`, and `SELECT INTO` retain writer
serialization. Rebuild requires shared readers to be closed. Shared mode requires
all participants to use the same mutex naming strategy and this coordination
protocol, on one host with working file-sharing locks and permission to create
lease files alongside the database. Do not mix concurrent direct connections or
older shared-mode implementations with these readers. Lease files must not be
removed while the database is in use.

## Failure ordering and tests

If a process dies during backfill, the complete WAL remains authoritative. If it
dies during truncation, the data flush has already completed. A killed reader
loses its OS lease; a killed writer's unconfirmed frames remain invisible. An I/O
failure in explicit checkpoint closes the engine so subsequent operations must
reopen and recover.

The `Mvcc*` tests cover untouched historical pages, version-zero readers,
multiple collection snapshots, page reuse, capture/registration races, concurrent
readers/writers/checkpoints, cleanup failures, shared query and transaction
lifetimes, and encrypted files. The process tests use the shared-mutex harness as
a child executable: readers at different versions, concurrent writers, reader and
writer termination, and termination during individual data writes, after the
data flush, and before/after WAL reclamation. In-memory recovery tests also clone
the exact data/WAL images at each interruption point before cleanup can repair them.

Run focused coverage with:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --filter FullyQualifiedName~Mvcc --settings tests.runsettings
```

The test build copies the process harness into its output. Process tests run on
the modern .NET targets; the remaining MVCC tests also compile for the .NET
Framework targets. These tests simulate process failures and write-boundary
interruptions, not arbitrary storage-controller corruption or torn sectors.
