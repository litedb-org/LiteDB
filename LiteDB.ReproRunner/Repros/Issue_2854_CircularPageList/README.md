# Issue 2854: circular Snapshot page-list walks

The repro creates a healthy database, verifies every target data page is reachable exactly once
from one of the collection page's five free-list heads, and then changes two raw little-endian page
header fields to close the longest list into a cycle. A second raw parser proves that no bytes outside
those fields changed and proves the exact cycle, including collection ownership, slot membership,
forward/backward edges, and reachability from the collection page. This keeps the corruption fixture
independent of LiteDB's traversal code.

Dedicated child processes first prove both healthy paths: the unmodified database can be dropped and
remains absent after reopening, and `$page_list` returns every raw-parser-identified data page exactly
once without changing the file. Two isolated five-second probes then exercise `DropCollection` and
the read-only `$page_list` system query against separate corrupt copies. After the deadline, process-
tree termination and output draining each have their own bounded grace period; the parent never makes
an unbounded wait. A timeout or recognized unsafe completion is reported as `BUG_2854_CONFIRMED`.
A crash, unrelated exception, missing operation-specific marker, or failed control is a harness
failure and cannot masquerade as either a reproduction or a repair.

Auto-rebuild is deliberately disabled for the probes: the tested contract is the corruption signal
that marks the file invalid and makes a later explicit auto-rebuild possible. Only a prompt
`LiteException` with error code 999 is considered safe. That exception deliberately changes LiteDB's
one-byte recovery marker; the fixed outcome requires that marker to become `1` while every other data
file byte and the exact circular topology remain unchanged.

Run package 5.0.21 and the current source tree:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2854_CircularPageList
```

Exit 0 plus `BUG_2854_CONFIRMED` means at least one bounded corrupt walk still failed the contract.
Exit 10 plus `VERIFIED_2854` requires both healthy controls, error 999 from both corrupt walks, and
the exact recovery-marker-only mutation.
Exit 20 is a fixture, crash, or unexpected-library failure. When fixed, change the latest manifest
expectation to exact exit 10 / `VERIFIED_2854` and set its state to green; accepting an arbitrary
nonzero exit would allow a crash or timeout to be mislabeled as a repair.
