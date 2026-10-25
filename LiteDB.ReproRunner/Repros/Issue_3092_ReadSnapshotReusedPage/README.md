# Issue 3092: A read snapshot reads a page its transaction reused for another collection

Reproduces [LiteDB issue #3092](https://github.com/litedb-org/LiteDB/issues/3092).

A legacy `BeginTrans` transaction (Direct mode, `TransactionPageLimit = 1`) counts
`source` through index `s`, pinning a read snapshot. Another thread drops `s` and
commits, freeing its pages. The transaction inserts into `target`, reusing freed page
IDs that a safepoint records as dirty, then counts `source` through `s` again for
every key. The known-bad build resolves the reused IDs to the transaction's own
`target` pages (`LiteException` 999 "page type must be index page, but it is Data").
Plain and encrypted files both run.

## Expected outcome

Against the known-bad LiteDB `6.0.0-prerelease.322` pinned in the `.csproj` the repro exits `0` and prints
`REPRODUCED` (the bug reproduces in both cases). Against the fixed in-repo source it exits `2`
and prints `FIXED_VERIFIED`: every key counts its 60 rows, the transaction commits and a freed
index page really was reused by `target`. Anything else exits `1`. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3092_ReadSnapshotReusedPage
```
