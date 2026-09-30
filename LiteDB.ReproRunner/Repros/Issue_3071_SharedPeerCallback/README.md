# Issue 3071: Shared callback waits forever for ownership retained by its own outer operation

Reproduces [LiteDB issue #3071](https://github.com/litedb-org/LiteDB/issues/3071), audit finding C12.

A Shared write runs its lazy input sequence while it retains the database's native mutex. The
sequence writes through a second Shared connection to the same file on the same thread. That
connection waits for the mutex, which is released only after the sequence returns. The repro
covers the write owning the mutex itself and a thread whose leased reader pins it, each plain
and encrypted, plus same-connection and other-database controls that must complete everywhere.
A custom stream is user code too: disposing a connection checkpoints through its data stream
while the mutex is still owned, and a peer write from that stream is covered plain and encrypted.

## Expected outcome

- Known-bad `6.0.0-prerelease.319` (built from `dev` `5dd942a73`): exit `0` with `PROVEN_BLOCK`.
  Every nested write (six cases) is still blocked after 5 s while the worker is inside its callback and the
  peer connection is queued for the native mutex. A timeout alone does not count. Earlier
  prereleases were not bisected.
- Fixed source: exit `2` with `FIXED_VERIFIED`. Every nested write is refused promptly with
  `InvalidOperationException`, the aborted outer write leaves no rows, the dispose completes, both connections stay
  usable, and a cold reopen finds exactly the expected rows through their index plus the sentinel.
- Anything else exits `1` and satisfies neither variant.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3071_SharedPeerCallback
```
