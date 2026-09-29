# PR 3027: a shared connection cannot open a database in a directory with a long escaped name

A regression since 5.0.21, found in [PR #3027](https://github.com/litedb-org/LiteDB/pull/3027) and fixed
by slice S02 of [#3051](https://github.com/litedb-org/LiteDB/issues/3051) (branch `split/02-unix-mutex-names`).
Shared mode names its mutexes after the URI-escaped full path of the database (#2709). The SHA-1
fallback that keeps a name short ran only on Windows, so on Linux and macOS a path whose escaped form
exceeds the runtime's named-mutex limit made every shared open throw. Non-ASCII directory names reach
the limit quickly: each Cyrillic letter escapes to six characters. 5.0.21 always used the SHA-1 of
the path, so the same database opened in shared mode.

The repro opens, writes and reopens a database with `Connection=shared` in a temporary directory
named with 48 Cyrillic letters (324 characters once escaped). Unix only.

## Expected outcome

- Known bad, LiteDB `6.0.0-prerelease.319` (published from `dev` at `5dd942a7`): exit `0`. The shared
  open throws `ArgumentException` "The length of the name exceeds the maximum limit." from the
  `Mutex` constructor.
- Candidate (the in-repo source of the S02 branch): exit `1`. The database opens, is written and reopens.
- Any other outcome exits `2` and fails the proof.

The permanent guard is `SharedMutexNameLength_Tests`; see `.github/safety/regression-proofs.json`
and `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3027_SharedMutexNameLength
```
