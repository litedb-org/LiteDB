# Issue 2059: larger indexed lookup and Windows Framework control

The reported 5.0.10 package and current source each run three fresh processes.
On Windows the worker targets **.NET Framework 4.8, x86** and requires a real
32-bit process. Linux uses .NET 8. Output records the worker target, CLR, bitness,
and loaded LiteDB assembly target; unexpected library targets fail the run. The 5.0.10 Windows package selects its
.NET Framework asset, whereas current source supplies its netstandard2.0 build.

Each process creates over 15 MiB of valid records using the three reported invoice
numbers and exact predicate. Four rounds of variable-size updates, deletes,
inserts, checkpoints and reopens must preserve the entire external ledger.
Indexed queries must return exactly the expected matches and newest record.

A second process then holds a real exclusive read handle, modeling the external
backup locking mentioned in the report. A normal database open must receive an
`IOException` with the platform sharing/locking code. After releasing the handle,
the data-file hash must be unchanged, all rows and queries must still match,
a recovery insert must persist, and another read-only reopen must preserve every
row and every file byte. This is a file-sharing control, not evidence that the
customer's backup created the damaged page.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- run Issue_2059_FrameworkLookup --report /tmp/issue-2059.json --report-format json
```

Exit 10 means the controls passed without reproducing the reported segment
failure. Any unexpected exception, data mismatch, wrong runtime, or timeout exits
20 and needs diagnosis. No damaged page or partial WAL is manufactured. The
original Windows 7/.NET 4.6 runtime, full customer model/settings, damaged file,
and preceding storage history remain unavailable; this control does not claim
to reproduce that environment or establish that its corruption is fixed.
