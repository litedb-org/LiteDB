# Issue 2797: LIKE wildcards

Runs scalar and collection predicates, before and after indexing, against an independently
constructed anchored CLR regex over an ASCII corpus. Includes empty strings, negative
matches, consecutive percent signs and underscores after percent signs. Each tiny case
has a two-second bound; a hung worker is a background thread and the repro process exits.

Exit 0 plus `BUG_2797_CONFIRMED` identifies a result mismatch or nontermination.
Exit 10 plus `VERIFIED_2797` requires every independent assertion to finish successfully.
Exit 20 is a harness or unexpected library failure and must never count as fixed.

Run both versions:

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2797_LikeWildcards
```

When fixing the bug, change the latest expectation to `kind: noRepro`, `exitCode: 10`,
`logContains: VERIFIED_2797` and state to green. Retain the exact exit code and marker:
the runner's default acceptance of any nonzero exit would also accept a crash.
