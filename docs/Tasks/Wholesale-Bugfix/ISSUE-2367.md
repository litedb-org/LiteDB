# #2367: environment-aware captured-member contract

Prepared from `0c4c3f4a1fb11ddd6df5884d058f250158953904` for a later immutable runtime. This adds one exact contract while preserving all previous twenty-seven. No source implementation, frozen-test change, failure-normalization change or source-observation exception is included.

## Evidence

The three issue cases fail in all six original baseline reports and repeat unchanged in all six unrelated #2874 candidate reports. Two genuine neighboring controls pass in all twelve reports. Original source/tests are `dd937719f7eee53c512f50ac604cab639bf42a4c`; the unrelated repeat candidate is `79e5c69cb9e6a1bcc08e919c2993aaa5401148b7`, not a #2367 fix. Run [34988724926](https://github.com/litedb-org/LiteDB/actions/runs/34988724926) used workflow `7629236e73bde318e934509244c3e61ef820d8db`. Each source/test/workflow execution identity and both raw report hashes were checked locally.

[issue-2367-focused-baseline.json](../../../scripts/bugfix/fixtures/issue-2367-focused-baseline.json) retains every test ID, exact canonical failure string per observed OS/runtime, source blob, artifact ID and both report hashes. The existing canonical-failure parser removes stack locations; #2367 has no case-specific normalization rule. Its canonical failures contain only the exception line. The contract binds full canonical failure SHA-256 as well as the first line; accepting only an exception type is insufficient.

| Environment | Artifact ID | Artifact name |
| --- | --- | --- |
| linux-x64-net8.0 | 10405370121 | bugfix-check-ubuntu-latest-net8.0 |
| linux-x64-net10.0 | 10405505060 | bugfix-check-ubuntu-latest-net10.0 |
| windows-x64-net8.0 | 10404073794 | bugfix-check-windows-latest-net8.0 |
| windows-x64-net10.0 | 10404574219 | bugfix-check-windows-latest-net10.0 |
| macos-arm64-net8.0 | 10405111338 | bugfix-check-macos-latest-net8.0 |
| macos-arm64-net10.0 | 10405450249 | bugfix-check-macos-latest-net10.0 |

## Genuine controls

- `LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Math_Eval` captures local `u` inside the expression (`x => u.Id + 10 * 2`, grouped arithmetic, Math.Abs and Math.Round). It calls BsonMapper.GetExpression, executes the generated expression against a mapped document and asserts exact result counts and values 25, 30, 15 and 1.67. This independently covers working captured scalar member evaluation through the same visitor.
- `LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Document_Navigation_Eval` uses the same executable assertion helper for the root document, nested Address and Address.Street, and literal-prefix concatenation. It protects genuine row-dependent member translation from being prematurely evaluated as a captured constant.
- `Linq_Array_Navigation_Eval` is deliberately excluded: its assertions are commented out. Neither control claims to prove collection-element capture works before the fix; those three exact red cases establish the missing behavior. Both controls belong to the shared LINQ member-evaluation component, so there is no unacknowledged component control gap.

## Selection and scope

Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2367_|FullyQualifiedName=LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Math_Eval|FullyQualifiedName=LiteDB.Tests.Mapper.LinqEval_Tests.Linq_Document_Navigation_Eval`.

Only `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs` is approved. Profile `compressed-acceptance-v6` adds an exact #2367/path pair and requires Ubuntu net8 and net10 focused+broad plus production builds. Managed expression translation has no identified OS-specific path or persisted encoding change, so no Windows or generic compatibility run is added. All six actually observed environments are approved, including macOS arm64; unobserved macOS x64 is not represented as evidence. Scope expansion fails unless separately reviewed, after which unknown paths retain conservative fallback. Full matrix remains the final sweep gate.

Frozen test sources:

- `LiteDB.Tests/Issues/Issue2367_Tests.cs`: `685e88b9f143473995b39bcdcb9d769dfd613017`.
- `LiteDB.Tests/Mapper/LinqEval_Tests.cs`: `ed40e4af19ba2fd516cb3f46baa880e8bcbc2874`.

## Exact regression classifications

### `LiteDB.Tests.Issues.Issue2367_Tests.Captured_element_date_member_matches_CLR_and_tracks_changed_values(kind: "array")`

Test ID: `78165f0a-0c44-8165-e5b0-d01e281d14b9`.

- **net8.0**: `System.Reflection.TargetException : Object does not match target type.`
  - Full canonical SHA-256: `7f05931bc41ec79a8ffc85930c1874b74f5661422b4f8c7ee895bac9959f3d29`.
- **net10.0**: `System.Reflection.TargetException : Object type LiteDB.Tests.Issues.Issue2367_Tests+Video does not match target type LiteDB.Tests.Issues.Issue2367_Tests+Video[].`
  - Full canonical SHA-256: `063e77577079e8fc4a5e0b6c9d83b25b3ce5361c8a91dc9264372057a6fd572b`.
### `LiteDB.Tests.Issues.Issue2367_Tests.Captured_element_date_member_matches_CLR_and_tracks_changed_values(kind: "dictionary")`

Test ID: `9918e282-9732-6472-1dcf-01cf2f283b51`.

- **net8.0**: `System.Reflection.TargetException : Object does not match target type.`
  - Full canonical SHA-256: `7f05931bc41ec79a8ffc85930c1874b74f5661422b4f8c7ee895bac9959f3d29`.
- **net10.0**: `System.Reflection.TargetException : Object type LiteDB.Tests.Issues.Issue2367_Tests+Video does not match target type System.Collections.Generic.Dictionary`2[System.String,LiteDB.Tests.Issues.Issue2367_Tests+Video].`
  - Full canonical SHA-256: `ec4b00e3cce8d7a29fe315941ebec40d28dcf15c21d3c65fb3c07879c3012aa9`.
### `LiteDB.Tests.Issues.Issue2367_Tests.Captured_element_date_member_matches_CLR_and_tracks_changed_values(kind: "list")`

Test ID: `b194e37f-e501-19e4-52fb-54c02d27dce7`.

- **net8.0**: `System.Reflection.TargetException : Object does not match target type.`
  - Full canonical SHA-256: `7f05931bc41ec79a8ffc85930c1874b74f5661422b4f8c7ee895bac9959f3d29`.
- **net10.0**: `System.Reflection.TargetException : Object type LiteDB.Tests.Issues.Issue2367_Tests+Video does not match target type System.Collections.Generic.List`1[LiteDB.Tests.Issues.Issue2367_Tests+Video].`
  - Full canonical SHA-256: `236833b3f915a2725320b382d425cc25bf8e871caf0edad4741ad491410763e4`.

Every OS maps to the classification for its actual runtime. The type-specific net10 array/list/dictionary messages must not be exchanged, nor accepted on net8. A green candidate must retain all five exact identities and pass every case. A fresh integration baseline is mandatory: earlier visitor fixes may already affect this report.

## Review requirements delivered in task.json

- **behavior**: Evaluate captured list/array/dictionary element member access against its actual object, preserving DateTime comparisons and replacement values across repeated use of the same predicate. Keep genuine document-dependent member access translated for each row; never execute it prematurely as client code. Linq_Math_Eval independently asserts captured u.Id arithmetic and Math calls; Linq_Document_Navigation_Eval asserts root, nested document and string behavior through GetExpression and Execute. Linq_Array_Navigation_Eval is vacuous and is not an approved control.
- **behavior**: Review interaction with #2770/#2779/#2322/#1829 in the same visitor and retain all accepted cases. A fresh integration baseline must still demonstrate this exact defect; an already passing case requires explicit prior-fix disposition. This contract grants no source-observation exemption: any transition of source guard 53 or another unrelated frozen test requires separate review and blocks automatic acceptance.
- **lifecycle**: Preserve captured value freshness when a list, array or dictionary entry changes, without a shared mutable cache or retained result from an earlier translation. Keep client evaluation and row-dependent execution boundaries clear; do not suppress reflection failures globally or mutate the closure or persisted documents.

## Remaining acceptance boundary

This resolves the old environment-signature deferral recorded in WAVE-4.md. Source guard 53 observes the same visitor member-stack behavior and may transition under a real fix. No exception is granted here: any unexpected source-guard or other test pass remains a comparator rejection pending a separate reviewed disposition. Do not confuse this prepared execution contract with unconditional readiness for automatic integration.

Validation includes twelve actual historical focused baseline/repeat gates, original blob and artifact identity checks, adversarial exact-runtime/full-message/test-ID mutations, controls/selection checks, preservation of the old twenty-seven contracts, compact profile selection and task.json note/hash delivery. No CI or worker was dispatched.

Local suites passed: **122 gate tests and 210 controller/profile tests**.
