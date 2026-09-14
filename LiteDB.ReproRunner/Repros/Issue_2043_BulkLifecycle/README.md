# Issue 2043: bulk writer and timer cleanup

This comparison retains the public usage pattern on the reported 5.0.8 package
and current source. Each variant runs three fresh processes. A process creates
a records database exceeding 50 MiB and a separate FileStorage database, then
runs one background bulk writer, a transactional Timer cleanup, and two ordered
readers. It performs 24 batches of 32 inserts with Int64 auto IDs, starting near
the reported ID range. Two rebuilds drain active operations first.

Independent sequence/payload and acknowledged-deletion ledgers verify every
survivor, every deleted primary key, secondary-index results, and every stored
file. Ordered pages are compared with an independently sorted projection in
the same read transaction. Both rebuild boundaries, a recovery insert after
reopen, and another read-only reopen must preserve the complete records ledger;
the read-only open must also preserve every file byte.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2043_BulkLifecycle --report /tmp/issue-2043.json --report-format json
```

Exit 10 means the controls pass without reproducing the report. A timeout,
exception, or data mismatch exits 20 and needs diagnosis; it is not automatically
classified as the original duplicate-auto-ID failure. The private damaged file,
original complete model and connection settings, unsynchronized concurrent
rebuilds, and the originating storage history remain outside this control.
