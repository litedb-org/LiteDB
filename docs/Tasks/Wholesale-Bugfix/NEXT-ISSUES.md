# Next canaries

Prepared for a future runtime revision after issue #2874 completes acceptance and
integration. Preparation does not authorize the controller to skip those gates.
The active runtime-v2 reference is unchanged.

The [execution manifest](../../../scripts/bugfix/issues.json) now contains #2839
and #2869. Existing #2874 execution metadata is unchanged. Both new contracts use
the original regression files from `dd937719f7eee53c512f50ac604cab639bf42a4c` and
the same eight approved OS/architecture/framework environments as #2874.

| Order | Issue | Exact test filter | Expected baseline | Allowed production file |
| --- | --- | --- | --- | --- |
| 1 | #2839: LongCount(Query) preserves Int64 width | `FullyQualifiedName~Issue2839_Tests` | 1 failed, 2 passed | `LiteDB/Client/Database/Collections/Aggregate.cs` |
| 2 | #2869: BSON Int32 implicitly widens | `FullyQualifiedName~Issue2869_Tests` | 5 failed, 1 passed | `LiteDB/Document/BsonValue.cs` |

## Contract boundaries

For #2839, the original engine double independently checks collection, aggregate
projection, predicate parameters, order, offset, and limit. Separate passing
controls pin the Int32 Count(Query) path and the unfiltered LongCount path. The
target route returns 4,294,967,303, so discarding the query cannot satisfy it.
Review neighboring count overloads and preservation of caller query state.

For #2869, the original five theory cases cover Int32 minimum, -1, 0, 1, and
maximum after BSON serialization/deserialization. They check numeric values and
preservation of the stored BSON type. The separate control checks native Int64
and Double conversions and rejects numeric strings. Review numeric boundaries,
source immutability, and neighboring implicit operators. `BsonValue.cs` has a
non-growing file-size exception.

The manifest contains exact parameterized display names, per-case failure first
lines, control names, and original Git blob IDs. No test edits are needed to load
either issue with the existing gate schema.

## Local preparation evidence

On 2026-09-15, both focused selections ran against the existing Release .NET 10
build in the clean, detached original-baseline worktree. These runs used
`TestingEnabled=true`, `--no-build --no-restore`, and `tests.runsettings`.

| Issue | Actual result | Reported test duration | TRX SHA-256 |
| --- | --- | --- | --- |
| #2839 | 1 failed, 2 passed, 0 skipped | 45 ms | `623b47112885d0dd465356235ce34badc429948e4f861f1ff37444a626e1bb27` |
| #2869 | 5 failed, 1 passed, 0 skipped | 24 ms | `2db84241e279edf2914f3c02be2e34dd2ff6e4c173177f48e0dad21eacbcb098` |

Both actual TRX files satisfy the new baseline gates in
`windows-x64-net10.0`. Both original source-blob checks also pass.
These are local preflight checks of uncommitted future contracts; they are not
CI acceptance evidence. The future runtime must commit its policy, rebuild the
pinned integration base, reproduce the defect, and bind fresh evidence to its
own revision before dispatching a fix worker.

Original behavioral evidence is documented in
[issue 2839](../../open-bugs/2839.md) and
[issue 2869](../../open-bugs/2869.md).

## Deferred alternative

Issue #2858 remains a later option. All four original culture-regression cases
fail; uppercase controls are embedded inside them rather than separate passing
test identities. Its rebuild and transaction paths also need broader validation
than the two selected canaries.
