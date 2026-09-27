# Issue #2991: reader ownership after thread retirement

[Issue #2991](https://github.com/litedb-org/LiteDB/issues/2991) reports a checkpoint
`LockRecursionException` after the cross-thread cursor disposal fix in #2790.
The nightly failures occurred in the `concurrent` target on .NET 10, at steps
416 and 218 for seeds 1681725734 and 1682725737 respectively.

## Root cause and reproduction

The original code keyed transaction reader leases by `Environment.CurrentManagedThreadId`.
A query can outlive its opening thread: an async continuation can run on a
short-lived checkpoint thread, open the next cursor, and yield before draining it.
When that thread is collected, its numeric ID can be reused by a new checkpoint
thread. The reader lease still belongs to the old cursor, but the gate mistakes
the new checkpoint thread for its owner and rejects an apparent read-to-write upgrade.

Local runs of both original seeds reproduced the same exception before the fix,
at steps 178 and 67. Temporary weak-reference instrumentation at admission proved
that the original thread object had been collected and the failing checkpoint
was a different thread (`originalAlive=False`, `sameThread=False`, one reader lease).
The shorter prefix is schedule dependent; it is not a deterministic minimum.

`Issue2991_Tests.Retired_reader_owner_does_not_make_new_threads_recursive_writers`
reduces the problem to retired reader owners, forced GC, and fresh writer threads.
It failed against the original gate on .NET 10: a new thread reported holding a
reader lease. Its assertions remain valid on runtimes that do not recycle IDs
in that execution. The companion engine regression transfers real cursors,
checks transaction lookup, disposes them from independent explicit transactions,
rolls those transactions back, and verifies that checkpoint is available afterward.

## Fix and boundaries

The gate retains `Thread` identities for readers and its exclusive owner.
Transactions retain the same identity for release, lookup, page cleanup, and
foreign completion checks. Numeric IDs remain available in diagnostic system
collections, but no engine ownership decision compares them.

A real reader owner still cannot upgrade to exclusive mode. Other writers still
wait for all leases, and foreign cursor disposal still releases only that cursor's
lease. The gate removes its owner reference with the final reader release or
shutdown. This changes internal ownership representation; it does not add an
on-disk format or public API change, nor allow explicit transactions to span threads.

## Ongoing coverage

- `transaction-gate`: randomized nested leases and foreign release order checked
  against an independent count model, self-upgrade rejection, and reader/writer exclusion.
- `cursor-handoff`: retired owners and GC, fresh-thread transaction lookup, snapshot
  contents after writes, early disposal versus full drain, rollback isolation,
  checkpoint overlapping the final release, rebuild, and raw-file integrity.
- Both targets record randomized decisions and check replay hashes. Actual thread
  scheduling and numeric IDs are not deterministic inputs.
- The permanent corpus pins both targeted seeds and the two observed failing
  `concurrent` prefixes. Linux smoke exercises .NET 8 and .NET 10; nightly persistence
  includes both new targets.

See [the fuzz runner guide](../LiteDB.Fuzz/README.md#reader-ownership-regression-2991)
for a focused campaign command.

## Validation on Linux x64

- Release solution restore/build with `TestingEnabled=true`: passed.
- Production `netstandard2.0` build with separate output and `TestingEnabled=false`: passed.
- Full `LiteDB.Tests`, using `tests.runsettings`: 3,069 passed and seven existing
  skips on each of .NET 8 and .NET 10.
- Original concurrent seeds: 1,500 rounds each passed on .NET 10, including
  12,000 acknowledged counter updates and 3,000 unique-key contention winners.
- Both new targets passed deterministic input/trace replays on .NET 8 and .NET 10.
- Permanent corpus replay and changed C# file-size/whitespace checks passed.
- Two-minute .NET 10 campaign: 18 successful target/epoch runs,
  1,253,411 modeled foreign lease releases and 48,267 cursor handoffs,
  with no failures. These bounded campaigns supplement the ownership regression;
  they do not exhaust all thread schedules.
