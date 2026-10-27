# Issue 3124: LINQ floating-point negation loses signed zero

Reproduces [LiteDB issue #3124](https://github.com/litedb-org/LiteDB/issues/3124).

Found by review round 2, F1 (roadmap #3124). Regression from PR #2971:
`1d / -(-x.D) < 0d` over `+0d, -0d, +1d` returns `[]` in
`6.0.0-prerelease.355` (repository commit `b3c440623190`), versus `[2]` in CLR
and the candidate. The float case has the same outcome. In 5.0.21 two ignored
minuses happened to cancel correctly; the single-minus case was already wrong.
The repro checks both cases, with fixed and known-bad row signatures.

## Expected outcome

Against the known-bad LiteDB `6.0.0-prerelease.355` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `1` with a fixed-result marker.
Unexpected exceptions exit `2` and fail validation. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3124_LinqSignedZero
```
