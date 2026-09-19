# Snapshot-aware checkpointing

An ordinary query pins its logical read version for its entire lifetime, including
pages it has not visited. Checkpoint backfills the newest committed image of each
page at or below the oldest live snapshot. It does not wait for readers to finish.
`Checkpoint()` returns the number of pages copied; with a version-zero reader it
can return zero. Dispose streaming readers promptly to permit full WAL truncation.

## Backfill and reclamation

Checkpoint uses the minimum of the current commit version, every local collection
snapshot, and all live shared-reader versions. Capture/registration, commit-index
publication, and boundary selection share the WAL index lock. Registration in
other processes is ordered by the named database mutex.

For each page, keep its newest committed version at or below the watermark (its
**floor**) and every newer version. Earlier versions are obsolete. For example,
with page versions 2, 6, 9 and watermark 8, version 6 must remain available even
though it is below 8; version 2 can be reclaimed. A commit marker also remains
until none of that transaction's page versions are required.

An active snapshot S is at least the watermark. Its index was captured after the
floor committed, so it cannot select a frame older than that floor. This holds
for another process's unchanged index and for a reader suspended after resolving
an offset but before reading its bytes. If no WAL version exists at or below S,
backfill at or below S cannot change that page's original data-file image.

The physical protocol is:

1. Flush the WAL, copy the safe page images, and durably flush the data file.
2. Invalidate obsolete cached frames and overwrite their WAL slots with zero
   pages, without moving any retained offset.
3. Durably flush the cleared slots before publishing them to the free-slot pool.
4. Let subsequent unconfirmed writes reuse eligible slots under the WAL writer
   lock. Failed reused writes are not immediately returned to the pool; reopening
   only recovers slots that are entirely zero. Abandoned nonzero frames remain
   until full reset. Transaction-private safepoint reuse remains supported.
5. Append confirmation frames. Their page positions define logical version IDs;
   removing old transactions therefore never renumbers another process's lease.

### Recovery and compatibility

Recovery rebuilds the free-slot pool from zero pages. Retained floor frames and
their confirmation records reconstruct every required page version. Versions are
sparse confirmation-position IDs, not counts of surviving confirmations. An
interruption during clearing can leave some obsolete frames present or some old
transactions without confirmations; required page images and their confirmations
remain intact. There is no separately published persistent base or free-list file.

Existing v8 engines checkpoint in physical WAL order. A reclaimed slot is eligible
only if it comes after this page's previous WAL positions, so physical order per
page still agrees with commit order. Confirmations always append after their
transaction's frames. This restriction deliberately preserves ordinary v8
compatibility and v9 vector compatibility without introducing a format migration.
The compatibility script opens a reclaimed/reused WAL in LiteDB 5.0.21, updates
it, checkpoints it, and reopens it in both engines, plain and encrypted.

### Space savings and limits

Reclamation provides reusable capacity inside the existing WAL. It does **not**
shrink the live file or punch filesystem holes. Full truncation, index reset, and
version-counter reset require exclusive local transaction ownership and no shared
reader leases, and happen only after the data flush.

Retained floors, newer versions, and necessary commit markers cannot be reclaimed.
Not every freed slot is eligible for every page: a page whose previous frame is
near the WAL tail may still need to append, preserving legacy replay order.
Consequently this reduces growth when usable obsolete slots exist, rather than
guaranteeing bounded storage for arbitrary long-running readers or hot-page writes.

A Linux measurement using the process-test fixture seeded two collections of 64
rows with 3,000-byte payloads, updated one collection 20 times, and kept a reader
open while updating the other collection five times. With checkpoint disabled for
the control, WAL growth was 1,351,680 bytes; after partial checkpoint/reclamation,
it was 73,728 bytes: **94.5% less incremental growth**, for both plain and encrypted
files. The initial WAL was about 6 MB and stayed allocated in both runs. This is a
workload-specific capacity-reuse measurement, not a general storage reduction.

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

If a process dies during backfill, the required WAL versions remain authoritative. If it
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
data flush, before/after WAL truncation, and during slot clearing, flush, and
free-slot publication. Tests also pause a reader between offset resolution and
page access while other writes reuse reclaimed slots. In-memory recovery tests also clone
the exact data/WAL images at each interruption point before cleanup can repair them.

Run focused coverage with:

```sh
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --filter FullyQualifiedName~Mvcc --settings tests.runsettings
```

The test build copies the process harness into its output. Process tests run on
the modern .NET targets; the remaining MVCC tests also compile for the .NET
Framework targets. These tests simulate process failures and write-boundary
interruptions, not arbitrary storage-controller corruption or torn sectors.
