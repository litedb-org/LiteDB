# Issue 2163: mixed Direct and Shared ownership loses an acknowledged write

This Linux-only two-process repro covers
[issue #2163](https://github.com/litedb-org/LiteDB/issues/2163). Process 0 opens
the file in Direct mode, begins an explicit transaction, and reads one baseline
row. While that owner and its stale snapshot remain live, process 1 opens the
same path in Shared mode, inserts row 202, checkpoints it, reopens the database,
and verifies the row through both `_id` and a unique token index.

Only after process 1 has published that receipt does the Direct owner insert row
303, commit, checkpoint, and release the file. On affected builds, both writers
report success, but the Direct checkpoint replaces the Shared generation. Two
fresh reopens contain rows 101, 303, and the post-release Shared control 404;
the externally receipted row 202 alone is gone.

The shared run directory contains fsynced JSON receipts written immediately
after acknowledged operations. The final oracle validates every field and a
SHA-256-derived payload digest, the primary key, the unique secondary index,
the exact missing/unexpected ID sets, and a second reopen after checkpoint.
Process 1 also performs a non-conflicting Shared insert after Direct ownership
has ended. This prevents a fix that disables Shared writes, suppresses an
exception, repairs only in-memory visibility, or drops arbitrary rows from
making the repro green.

Run package 5.0.21 and the current source tree with:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
  run Issue_2163_MixedAccess
```

The following are accepted fixed outcomes, provided every external receipt is
present on both final reopens:

- Shared fails fast with an ownership-specific error before inserting.
- Shared waits until the Direct owner releases the file.
- Direct rejects its stale mutation before acknowledging it.
- Both overlapping writes succeed and all rows remain durable.

Exit `0` plus `BUG_2163_CONFIRMED` is reserved for the exact acknowledged-row
loss. Exit `10` denotes one of the safe outcomes above. Exit `20` denotes a
protocol, fixture, unrelated exception, or database-ledger failure and must not
be treated as a fix. When the bug is repaired, change the latest manifest
expectation to exact exit `10` and its specific `NO_BUG_2163_*` marker rather
than accepting any nonzero result.

Last verified on Linux x64 against package 5.0.21 and source commit
`3c0840ae`: both variants produced the exact loss and matched the manifest.

Rechecked on 2026-09-14 after merging dev `a50661a9`: both 5.0.21 and current
source still acknowledge row 202 and then lose exactly that receipt on both
fresh reopens. Each process emits `BUG_2163_CONFIRMED`; the passing manifest
comparison records successful reproduction, not repaired durability.

The manual ownership fix rejects the overlapping Shared operation before it
acknowledges a write (`NO_BUG_2163_SHARED_REJECTED`, exit `10`). The latest-source
manifest now expects that outcome; package 5.0.21 retains the exact lost-receipt
expectation. Both final reopens and the post-release Shared insert are checked.
