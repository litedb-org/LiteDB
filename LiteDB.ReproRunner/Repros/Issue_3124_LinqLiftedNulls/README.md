# Issue 3124: LINQ lifted nullable comparisons include null rows

Reproduces [LiteDB issue #3124](https://github.com/litedb-org/LiteDB/issues/3124).

Found by review round 2, F4 (roadmap #3124). Pre-existing in 5.0.21:
`x.N < 5` selects a null-valued row because BSON null sorts below numbers.
The repro checks all four relations, equality/inequality null controls, and
Boolean composition, against CLR and explicit known-bad row signatures.

**Behavior change from 5.0.21:** LINQ lifted relational comparisons now return
false on null operands. Raw BSON/SQL total-order comparisons are unchanged.

## Expected outcome

Against the known-bad LiteDB `5.0.21` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits `1` with a fixed-result marker.
Unexpected exceptions exit `2` and fail validation. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3124_LinqLiftedNulls
```
