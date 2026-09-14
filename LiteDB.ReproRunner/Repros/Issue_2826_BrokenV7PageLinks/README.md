# Issue 2826: broken v7 extend-page links

The checked-in 40,960-byte database was created by LiteDB 4.1.4 and contains an
inline control document plus a 20,000-character document spanning five extend
pages. Before invoking LiteDB, an independent raw parser verifies the file's
SHA-256, v7 header, collection and index ownership, data-block references,
every extend link and payload length, and the exact BSON fields of both rows.

Two copies then change only the four-byte `NextPageID` at offset 9 of the first
extend page. One points back to that same page and one points beyond the physical
file. The mutator rereads each file and rejects any change outside that field.
A third byte-identical copy is upgraded first as a healthy control; both source
documents must survive an independent reopen with exact IDs, fields, and payloads.

Each corrupt upgrade runs in its own child process with a five-second deadline,
a 128 MiB managed-heap cap, a 256 MiB working-set cap, process-tree termination,
and bounded output draining. A timeout or memory limit counts only after the child
has emitted the operation-start marker and the parent proves the process exited
and all output was collected. The known `FileReaderV7` `OutOfMemoryException`,
"Stream was too long", and past-EOF `NullReferenceException` are classified
separately; unrelated exceptions and harness failures cannot make the case green.

A repair has two safe outcomes. It may reject the corrupt source with a
`LiteException`, provided the exact file and directory-sidecar inventory remains
unchanged. Or it may complete the upgrade, preserve the intact document exactly,
invent no rows, persist any surviving large row exactly, and write a nonempty
`_rebuild_errors` ledger that identifies the damaged page chain. Both corrupt
shapes must be safe; fixing only one is still a reproduction.

Run package 5.0.21 and the current source tree:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
  run Issue_2826_BrokenV7PageLinks
```

Exit `0` plus `BUG_2826_CONFIRMED` means at least one bounded corrupt upgrade
still crashes, loops, or exceeds memory. Exit `10` plus `VERIFIED_2826` requires
the healthy control and both complete fixed ledgers above. Exit `20` is a fixture,
protocol, unrelated-library, or cleanup failure. When repaired, change the latest
manifest expectation to exact exit `10` and `VERIFIED_2826`; never accept an
arbitrary exception or nonzero exit as a fix.
