# Snapshot-aware checkpointing

This is the v13 reclamation layer of native stack #3001:
#2998 checksums → #2924 index ordering → #2999 compact storage → #3000 MVCC.
It retains complete transaction verification while allowing unreachable WAL
payloads to be reclaimed. See [the format](mvcc-retirement-format.md).

An ordinary query pins its logical read version for its entire lifetime, including
pages it has not visited. Checkpoint backfills the newest committed image of each
page at or below the oldest live snapshot. It does not wait for readers to finish.
`Checkpoint()` returns the number of pages copied; with a version-zero reader it
can return zero. Dispose streaming readers promptly to permit full WAL truncation.

`Checkpoint()` therefore no longer guarantees an empty WAL. It waits only briefly
for open transactions and otherwise backfills what is safe. The data file alone is
a complete copy of the database only when the WAL is empty afterwards. Exclude
concurrent writers and checkpoints throughout backup preparation and capture.
Under that exclusive control, either close readers, complete a checkpoint and
verify an empty WAL before copying the data file; or capture the data and WAL as
one consistent filesystem snapshot. Keep the exclusion until capture finishes:
checking an empty WAL does not prevent a later write. Independently copying two
live files is not a consistency protocol.

An auto-checkpoint from a commit takes the full path whenever no transaction is
open and no snapshot or shared lease is live. Otherwise it does partial work on a
back-off (50 ms doubling to 1 s), because that work validates and scans the whole
WAL and requires multiple durable publication barriers. Only a reclaiming
checkpoint resets the back-off: holding transaction exclusion is not enough, since
snapshots and other connections' leases survive it. Shared mode opens and closes an
engine per operation, so one back-off outlives those engines and also rations their
close checkpoints; a close that can reclaim always runs. Explicit `Checkpoint()` is
never rationed.

## Backfill and reclamation

Checkpoint uses the minimum of the current commit version, every local collection
snapshot, and all live shared-reader versions. Capture/registration, commit-index
publication, and boundary selection share the WAL index lock. Registration in
other processes is ordered by the named database mutex.

A snapshot S resolves a page to its **floor**: the newest committed version at or
below S. For each page, keep the floor of every live snapshot (local and shared)
and the newest version, which is the floor of every snapshot taken later. Every
other version is obsolete, including versions newer than the oldest reader. For
example, with page versions 2, 6, 9, 12 and live snapshots 8 and 10, versions 6
and 9 are floors and 12 is newest; version 2 can be reclaimed. With one snapshot
at 8, version 9 is reclaimable too. A commit marker remains until none of that
transaction's page versions are required.

A snapshot's index was captured after its floor committed, so it cannot select
any other frame. This holds for another process's unchanged index and for a
reader suspended after resolving an offset but before reading its bytes. Backfill
still stops at the oldest snapshot. If no WAL version exists at or below S,
backfill at or below S cannot change that page's original data-file image.

The checkpointer excludes commit publication with the header monitor, then takes
the index write lock and WAL writer monitor. This orders the live committed set
with the WAL being verified and excludes safepoint writes until copying and
publication finish. Shared leases are inspected under the database mutex before
these locks. Failed confirmed-flush teardown releases the writer monitor before
cleaning up snapshots; checkpoint failures stop the engine after releasing locks.

The physical protocol is:

1. Verify the complete WAL, including earlier retirement witnesses, and match its
   confirmed transaction IDs to the live committed set before modifying either file.
2. Before the first retirement, durably promote the data header to v13 through the
   existing WAL-bound header journal. This changes one header, not documents or indexes.
3. Append and sync retirement records retaining the original identity, position,
   contribution, count/digest and confirmation sequence of each obsolete frame.
   These records are not yet permission to overwrite anything.
4. Seal and sync a header recovery journal bound to the entire verified WAL.
   Copy safe page images and sync the data file.
5. Publish and sync the witness-chain root and minimum commit sequence in the
   checksummed data header. Durably remove the header journal before changing WAL bytes.
6. Invalidate obsolete cached frames, clear their payload slots, sync the clears,
   then publish reusable capacity. Retained offsets never move.
7. Reuse witnessed slots for nonconfirmation frames; confirmations always append.
   Their physical positions remain stable snapshot version IDs across processes.

### Recovery and compatibility

Recovery validates the chain rooted in header bytes 168–187 before trusting any
hole. It replays each retired frame's original witness at its original position,
then any current payload incarnation at that position. The existing transaction
count/digest and consecutive confirmation-sequence checks remain mandatory.
Missing/corrupt frames without witnesses still fail those checks. A torn new
unconfirmed incarnation at an authorized slot cannot invalidate an older commit;
if that incarnation was committed, its missing contribution invalidates its later
confirmation. A published root's minimum sequence prevents recovery from discarding
commits needed by partially backfilled data. Rebuild uses the same witness reader
and transaction verifier. Read-only recovery preserves data and WAL bytes.

A transaction keeps its ID across interleaved commits. Recovery restores the
maximum observed ID, including witnesses and intact abandoned frames. Rewrites
within one transaction must keep increasing physical positions for each page;
otherwise holes created between safepoints could make physical replay select an
older image. Unconfirmed slots preceding an acknowledgement remain protected
against in-place rewrite. Different transactions can reuse a page's earlier slots.

New files retain the parent's BSON v11 / Auto compact v12 creation policy. Existing
v8/v9/v10 writable opens first perform the parent's checksum/index migrations.
v11/v12 files promote lazily on the first checkpoint that retires obsolete frames.
Promotion and witness publication are separate durable steps: an interrupted
attempt may leave a valid v13 file with no retirement root. A later writable open
recovers automatically; a subsequent checkpoint can retry reclamation. There is no
whole-database rebuild or background conversion for this layer. The inherited
index migration has separate index-rewrite and temporary-space costs.

Released engines and the preceding v12 engine reject v13. A WAL/data pair must
stay together while a root is present: even a data-only copy of the backfilled
watermark is not a supported backup. A full checkpoint clears the root and rotates
the WAL salt only after all data is durable, then resets the WAL. It does not
lower the file version. Explicit rebuild can produce the parent's BSON v11 or
compact v12 representation because it creates a new database without retained WAL
history; this does not restore compatibility with released v8/v9 engines.

### Space savings and limits

The 8 KiB logical address space and 64-byte WAL checksum trailers remain unchanged.
Retirement witnesses occupy 56 bytes each; up to 145 fit in one 8,256-byte WAL
frame. The initial version promotion and subsequent root publications use a
transient 16 KiB header journal. No per-page payload backups or database-wide data
rewrite are required. Witness pages remain append-only until full checkpoint;
small batches still cost one whole witness frame. Recovery retains witness
metadata rather than buffering old payloads, and scans the WAL to verify complete
transactions. Long-held readers can grow metadata and recovery work indefinitely.

Reclamation reuses allocated WAL capacity; it does not shrink the live file or
punch holes. Retained floors and newest versions cannot be reclaimed. Confirmations
always append, so single-page commits and workloads with insufficient reusable
slots still grow the WAL. Full truncation requires exclusive local transaction
ownership and no shared leases. Storage savings from the original legacy PR are
historical and are not measurements of this protocol.

## Shared mode

Ordinary `SharedEngine.Query` first reads the result under the mutex. A result of
at most 100 values and 64 KiB completes there and is returned from memory: it
holds no engine, lock or lease, and costs what a shared query cost before. A
larger result is abandoned and streamed as follows, from the same committed state.
A failure while producing row N is raised by the `Read` that would have returned
row N, as a streaming reader does. A larger result pays for up to 101 discarded rows.
Queries configured with `ReadTransform` skip speculative buffering and immediately
take the streamed path so user callbacks execute exactly once per produced value.

A streamed query uses a private read-only engine with a fixed WAL
index and an OS-held lease in `<database filename>-readers/`. Under the existing
named database mutex it opens/replays the database and publishes a lease whose
name contains the captured version. It then releases the mutex before returning
the reader. The engine and lease remain alive until reader disposal. Temporary
sort storage is private to each query, including when a sort spills to disk.

Writers continue to own the mutex through their operation and commit. Every
checkpointer inspects shared leases while owning that same mutex. The reader's
engine closes before releasing its lease, so neither streams nor cached WAL
offsets outlive their protection. A newly opened query replays newer commits.

A write from the thread that is iterating one of the same `SharedEngine`'s leased
readers (for example `foreach (var d in col.FindAll()) col.Update(d)`) pins that
instance's engine: a dedicated holder thread acquires the mutex and keeps it, and
the engine open, between that thread's calls. Reopening per write would replay a
WAL that the open reader keeps growing, which makes such a loop quadratic. Other
threads and processes wait for the mutex while the pin holds it.

A `Mutex` can only be released by the thread that acquired it, so the pin never
keeps a recursion on the iterating thread; any thread can end it. The holder
closes the engine and releases the mutex at the first moment without a running
operation, open write-query reader or pinned transaction of the owner, once:

- the owner's last leased reader is disposed, on any thread;
- another thread of the same instance needs the mutex;
- the owner has made no call for the idle limit (100 ms, or one engine close and
  reopen if that takes longer), which bounds the wait of other processes;
- the pin has held for its total limit (1 s, or ten engine closes and reopens),
  which gives waiting processes a turn during a long loop; the owner's next
  write pins again.

`Dispose` on any thread and the exit of the owner thread end it regardless of
open readers and transactions; an exited owner's transaction is reported to the
next caller as before. Writes from other threads or instances never pin.

When the last leased reader is disposed and the WAL is not empty, a writable
`SharedEngine` that can take the mutex without waiting opens and closes an engine,
so its close checkpoint removes the WAL as the pre-v13 reader's close did. The
data file alone is then again a complete database once every connection closes.
If a lease cannot be registered (for example, no permission to create the
`-readers` directory next to a read-only database, or an uninspectable registry),
the query streams from the writer engine under the mutex instead, as before v13.

An exclusively opened lease file is the liveness primitive. The process keeps its
handle open; another participant can only open it exclusively after the OS has
released that handle. Stale files are then deleted under the database mutex, and
the directory with the last of them. A lease that cannot be proven dead for any
reason (sharing violation, access denied, delete-pending) counts as live. If the
lease directory itself cannot be enumerated, checkpoint skips both backfill and
reclamation; an unknown registry state is never interpreted as no readers.
No PID reuse or heartbeat timeout can evict a paused but live reader. Interrupted
registration is safe because replay, registration, and checkpoint are ordered by
the same mutex. After a machine restart there are no surviving reader handles.

Explicit shared transactions, `FOR UPDATE`, and `SELECT INTO` retain writer
serialization. Rebuild requires shared readers to be closed. Shared mode requires
all participants to use the same mutex naming strategy and this coordination
protocol, on one host with working file-sharing locks. Without permission to
create lease files alongside the database, large results hold the mutex while
streaming. Do not mix concurrent direct connections or
older shared-mode implementations with these readers. Lease files must not be
removed while the database is in use.

Shared connections bind their absolute filename at construction, so later
working-directory changes cannot redirect an internal reopen. See
[shared-mode safety](shared-mode-safety.md) for durability diagnostics and
the combined shared-process storage tests.

## Failure ordering and tests

If a process dies during backfill, the required WAL versions remain authoritative. If it
dies during truncation, the data flush has already completed. A killed reader
loses its OS lease; a killed writer's unconfirmed frames remain invisible. An I/O
failure in explicit checkpoint closes the engine so subsequent operations must
reopen and recover. An uncertain confirmed-WAL flush publishes the fatal engine
state while holding the WAL writer lock, then releases that lock before synchronous
transaction and snapshot cleanup. This avoids opposing WAL-writer/WAL-index lock
order with a partial checkpoint while preventing later engine use.

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
Framework targets. `MvccRetirement*` additionally tests durable versus volatile bytes, torn physical
writes to records/headers/reused slots, repeated torn recovery, failed syncs,
CRC-valid malformed witnesses, full document/index oracles, and real-file rebuild.
`MvccSafepointRetirement_Tests` exercises new holes between active-writer safepoints.
The `mvcc-retirement` fuzz target randomizes these failures and repeated retirement
cycles; it joins the daily three-minute checksum campaign. Local artifacts can be
placed under `/dev/shm` to avoid sustained physical disk writes.

The fault model assumes successful durable flushes persist addressed bytes and
power-safe overwrite preserves previously synced bytes outside the write range.
Retirement barriers always attempt a real sync, and a failed sync stops the
engine with redo intact. Log storage that answers "cannot sync" (#2242) degrades
them to ordered OS-cache flushes, which survive a process crash but not power
loss, as before #2818; on such storage reclaimed slots are never reused, so the
WAL appends until a full checkpoint truncates it. Before an engine, including each
short-lived shared-mode engine, first reuses a slot found blank at open, it syncs the
raw log once. That sync proves the storage can sync and makes earlier non-durable
clears durable; storage that answers "cannot sync" degrades and appends instead. These tests do not promise recovery from arbitrary independent
damage to both data and required recovery evidence. CRCs detect accidental damage,
not deliberate tampering.

The actual v12 parent is tested in a separate process by
`python3 scripts/test-mvcc-compatibility.py`; its default revision is pinned so
this probe cannot accidentally run a v13-capable engine.
`python3 scripts/test-vector-compatibility.py` additionally exercises released
LiteDB 5.0.21. Both require data **and WAL** byte preservation on rejection.
