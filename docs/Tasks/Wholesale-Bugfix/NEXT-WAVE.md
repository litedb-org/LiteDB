# Next ordinary bugfix wave

Prepared on 2026-09-15. This is a reviewed-contract preparation queue after #2874, #2839 and #2869; it does not register or dispatch new workers. The active manifest is unchanged. No new CI was launched for this preparation.

## Scope and acceptance

Ten original reproduced issues have 44 exact cases: **32 failing regressions and 12 passing controls**, with identical outcomes and failure first lines in all six preserved baseline lanes. The order below favors smaller changes first and serializes overlapping LINQ visitor work. Historical-only, unconfirmed, intermittent, security and externally dependent reports are excluded from this ordinary wave.

Each future fix requires a fresh baseline against the current integration commit, unchanged frozen test blobs, all exact target cases green, a broad comparison against the matching baseline and accepted-case ledger, a production build, its selected compatibility/runtime/OS checks, and three independent reviews. Broad comparison must retain unrelated known failures exactly and explicitly disposition unexpected passes. Original evidence here is queue preparation, not acceptance of any future candidate.

Per-fix validation uses the trusted compressed profile selected from the exact production Git diff plus the immutable issue contract. Run focused and broad checks in the same selected job; broad already includes the listed relevant tests, so do not rerun those filters separately. The full repository matrix runs only after the whole sweep.

**Profile proposals below are not active policy.** The current planner has reviewed ordinary pairs only for #2874/#2839/#2869. Most proposed paths below therefore currently receive the conservative six OS/framework lanes plus compatibility. #2845 already matches the serialization rule. Before dispatch, commit reviewed issue/path rules and any explicit required environments at a new immutable runtime revision. Approved environments are a permitted set, not a requirement to run every lane. Scope expansion or runtime/platform-sensitive diffs can only increase the selected checks.

## Independent baseline provenance

- Original test and production baseline commit: `dd937719f7eee53c512f50ac604cab639bf42a4c`.
- Original inventory: [inventory.json](../../open-bugs/inventory.json); every selected issue has status `reproduced` in the frozen revision.
- Evidence run: [34988724926](https://github.com/litedb-org/LiteDB/actions/runs/34988724926), workflow commit `7629236e73bde318e934509244c3e61ef820d8db`.
- The enclosing acceptance run tested candidate `79e5c69cb9e6a1bcc08e919c2993aaa5401148b7`; this document extracts **baseline/broad.trx only**, whose independent execution records name the original baseline source and test SHAs.
- The enclosing run concluded failure. Its available baseline reports remain reproducibility evidence; this document makes no claim that this run accepted #2874 or the next wave.
- All six local baseline files were compared byte-for-byte with their GitHub artifacts; downloaded archive SHA-256 values also matched the GitHub digest metadata. Local directory: `artifacts_temp/bugfix-full-ci/v4-acceptance/`.

| Artifact name / numeric ID | Actual baseline environment | Baseline broad.trx SHA-256 | Archive SHA-256 |
| --- | --- | --- | --- |
| `bugfix-check-ubuntu-latest-net8.0` / [10405370121](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10405370121) | Linux/x86_64/net8.0 | `a4d3b367c661fa8768fbb8668d4a8827713618fa3b488f15f02ca1607c8bd56b` | `69a348873f82e088cae63dbdbb3a8037380c76e7ff3b313f4eaef50b654f9c89` |
| `bugfix-check-ubuntu-latest-net10.0` / [10405505060](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10405505060) | Linux/x86_64/net10.0 | `d4d7854e6d5e036da9d1ad92ac4f5aebe0a23b8033fe5a50f9793dcf18cbe423` | `71dd00fa18c6b2ea22d3ea964d522f96921b6ae09fd909c6669377c46a2b58cb` |
| `bugfix-check-windows-latest-net8.0` / [10404073794](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10404073794) | Windows/AMD64/net8.0 | `931cbd570f47bf6a376f11dde76c0ecf1399f9d742acf0f90eeb9463d781bdf0` | `8c8605df350456e121d7669618000b5510ea118bbd1ffc9749ef5fc45db87aa4` |
| `bugfix-check-windows-latest-net10.0` / [10404574219](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10404574219) | Windows/AMD64/net10.0 | `789212a8598feb82270173bbc4b6e152682e4b1177a51ab6f7ad67d1dcc0c08e` | `2eedc64bd717b57a97cd3ff1feef9582bd06d1830da95b6e8ddc0d2ecbc275f5` |
| `bugfix-check-macos-latest-net8.0` / [10405111338](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10405111338) | Darwin/arm64/net8.0 | `c069b53a2512adcd39c9ecf0bbf1d1329ff1371fa21228dbf4de3923340ee719` | `1d4e91cc47a182cfb38dd7d99d38b7cdf75e90688f63bbd67345ee75cc16571b` |
| `bugfix-check-macos-latest-net10.0` / [10405450249](https://github.com/litedb-org/LiteDB/actions/runs/34988724926/artifacts/10405450249) | Darwin/arm64/net10.0 | `487ac255e77785b858569e7dbbd93a1e5422db470aae03c8c0d71741709b4e08` | `2d3a6c300761ed060def951a4b89883159f6ae08dc44bcd68133b9181af23eae` |

Each case below belongs to the explicitly listed fully qualified class; concatenate that class, a dot and the exact displayed case suffix to obtain the full TRX identity. Theory argument spelling is part of the identity. `Failed` means required baseline regression; `Passed` means required independent positive control. First-line hashes are SHA-256 of UTF-8 bytes of the first line after CRLF normalization and trimming surrounding message whitespace, with no final newline. They preserve exact recorded values without normalizing assertion details. All six artifacts have identical selected identities, outcomes and hashes.

## Queue summary

| Order | Issue | Baseline regression / control count | Proposed compact lanes | Compatibility |
| --- | --- | --- | --- | --- |
| 1 | [#1506](../../open-bugs/1506.md) | 1 / 1 | Ubuntu net8 | No |
| 2 | [#1002](../../open-bugs/1002.md) | 1 / 1 | Ubuntu net8 | Yes |
| 3 | [#2802](../../open-bugs/2802.md) | 10 / 1 | Ubuntu net8 | Yes |
| 4 | [#2770](../../open-bugs/2770.md) | 1 / 1 | Ubuntu net8 + net10 | No |
| 5 | [#2867](../../open-bugs/2867.md) | 3 / 1 | Ubuntu net8 + net10 | Yes |
| 6 | [#2845](../../open-bugs/2845.md) | 7 / 1 | Ubuntu net8 + net10 | Yes |
| 7 | [#2847](../../open-bugs/2847.md) | 2 / 2 | Ubuntu net8 + Windows net8 | No |
| 8 | [#2779](../../open-bugs/2779.md) | 5 / 2 | Ubuntu net8 + net10 | No |
| 9 | [#2205](../../open-bugs/2205.md) | 1 / 1 | Ubuntu net8 | No |
| 10 | [#2871](../../open-bugs/2871.md) | 1 / 1 | Ubuntu + Windows, each net8 + net10 | No |

## #1506: Preserve a reusable Query when Find overrides paging

- Frozen source: [LiteDB.Tests/Issues/Issue1506_Tests.cs](../../../LiteDB.Tests/Issues/Issue1506_Tests.cs); Git blob `42722f965457a84ac385399b59707e1a7d9a9084`.
- Class: `LiteDB.Tests.Issues.Issue1506_Tests`.
- Selection: `FullyQualifiedName~Issue1506_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Database/Collections/Find.cs`.
- Proposed minimum profile: Ubuntu .NET 8; production build; no compatibility run if the diff only removes caller-query mutation.

Find(Query, ...) mutates the supplied Query. Preserve the entire projection, includes, predicate parameters, ordering, offset and limit through immediate, partial and complete lazy enumeration. The passing control preserves the existing omitted-paging behavior. If a reusable copy helper requires LiteDB/Client/Structures/Query.cs, obtain a reviewed scope expansion before changing it.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Find_honors_query_limit_when_optional_paging_is_omitted` | Passed | - |
| `Find_paging_override_preserves_the_complete_reusable_query` | Failed | `227317c37e82e9a02e7dac95dd04e848da66da8afc19b1c81cf31c238264580b` |

Observed baseline failure first lines:

```text
Expected query.Offset to be 1 because immediately after Find returns, but found 2.
```


## #1002: Generate integer IDs for null nullable-integer keys

- Frozen source: [LiteDB.Tests/Issues/Issue1002_Tests.cs](../../../LiteDB.Tests/Issues/Issue1002_Tests.cs); Git blob `300112d2fa9c10055460484549e551f7d750451e`.
- Class: `LiteDB.Tests.Issues.Issue1002_Tests`.
- Selection: `FullyQualifiedName~Issue1002_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Database/Collections/Insert.cs`.
- Proposed minimum profile: Ubuntu .NET 8; production build; compatibility because the change affects persisted primary keys.

RemoveDocId handles integer zero but leaves a null nullable integer as an invalid BSON _id. The zero case already passes. The regression checks two generated distinct integer IDs, assignment back to the entities, reopening, and raw persisted Int32 values. Keep the change at insertion mapping if possible; an engine change requires a new scope and profile review.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Nullable_integer_empty_ids_generate_unique_persisted_integer_keys(empty: 0)` | Passed | - |
| `Nullable_integer_empty_ids_generate_unique_persisted_integer_keys(empty: null)` | Failed | `f3d319412038c304a9098a8cf3ad28dd538eb40c2597d6157de42c0a0538f02d` |

Observed baseline failure first lines:

```text
LiteDB.LiteException : Invalid BSON data type 'Null' on field '_id'.
```


## #2802: Honor virtual mapper overrides during typed reads

- Frozen source: [LiteDB.Tests/Issues/Issue2802_Tests.cs](../../../LiteDB.Tests/Issues/Issue2802_Tests.cs); Git blob `d39406d4e8982b3081fb58149848907a25d97822`.
- Class: `LiteDB.Tests.Issues.Issue2802_Tests`.
- Selection: `FullyQualifiedName~Issue2802_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Database/LiteQueryable.cs`.
- Proposed minimum profile: Ubuntu .NET 8; production build; compatibility for deserialization behavior.

ToEnumerable calls Deserialize directly. Both non-generic and generic-only virtual mapping overrides must be called and their returned objects used across Find, FindAll, FindById, FindOne and Query. Preserve simple/scalar result handling. This file has a non-growing size exception.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Generic_only_mapper_control_differs_from_default_mapping` | Passed | - |
| `Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "Find")` | Failed | `573c8cb421464a0bd3740fe6771da3c9005e49070082a33cb66b02001bbfb3c6` |
| `Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "FindAll")` | Failed | `573c8cb421464a0bd3740fe6771da3c9005e49070082a33cb66b02001bbfb3c6` |
| `Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "FindById")` | Failed | `573c8cb421464a0bd3740fe6771da3c9005e49070082a33cb66b02001bbfb3c6` |
| `Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "FindOne")` | Failed | `573c8cb421464a0bd3740fe6771da3c9005e49070082a33cb66b02001bbfb3c6` |
| `Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(api: "Query")` | Failed | `573c8cb421464a0bd3740fe6771da3c9005e49070082a33cb66b02001bbfb3c6` |
| `Typed_read_uses_virtual_mapper_and_returns_its_result(api: "Find")` | Failed | `c4375225f93d02fde5a28655670cf586eaec459f35e05a9979d8af59de7a8d73` |
| `Typed_read_uses_virtual_mapper_and_returns_its_result(api: "FindAll")` | Failed | `c4375225f93d02fde5a28655670cf586eaec459f35e05a9979d8af59de7a8d73` |
| `Typed_read_uses_virtual_mapper_and_returns_its_result(api: "FindById")` | Failed | `c4375225f93d02fde5a28655670cf586eaec459f35e05a9979d8af59de7a8d73` |
| `Typed_read_uses_virtual_mapper_and_returns_its_result(api: "FindOne")` | Failed | `c4375225f93d02fde5a28655670cf586eaec459f35e05a9979d8af59de7a8d73` |
| `Typed_read_uses_virtual_mapper_and_returns_its_result(api: "Query")` | Failed | `c4375225f93d02fde5a28655670cf586eaec459f35e05a9979d8af59de7a8d73` |

Observed baseline failure first lines:

```text
Expected result.Name to be "generic:stored" with a length of 14, but "stored" has a length of 6, differs near "sto" (index 0).
```

```text
Expected result.Name to be "decoded:one" with a length of 11 because calling and discarding the override's result is insufficient, but "one" has a length of 3, differs near "one" (index 0).
```


## #2770: Translate row-dependent enum comparisons

- Frozen source: [LiteDB.Tests/Issues/Issue2770_Tests.cs](../../../LiteDB.Tests/Issues/Issue2770_Tests.cs); Git blob `6820452205a67c987a5556b2987aa0c3d5d9cc6d`.
- Class: `LiteDB.Tests.Issues.Issue2770_Tests`.
- Selection: `FullyQualifiedName~Issue2770_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs`.
- Proposed minimum profile: Ubuntu .NET 8 and .NET 10; production build. No compatibility run for a translation-only change.

VisitBinary tries to evaluate the right-hand expression independently when converting an enum operand, even when that expression references the row parameter. The string-backed case fails; integer-backed equality and inequality already pass. Preserve constant enum comparisons and do not evaluate row-dependent code as a captured constant. This file has a non-growing size exception.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Enum_property_comparison_matches_CLR_for_equal_and_unequal_rows(asInteger: False)` | Failed | `a36ee46c8398f3fe6a5489ec4a05a49329340eabfa6d7cdff8d9b89c0fe3917a` |
| `Enum_property_comparison_matches_CLR_for_equal_and_unequal_rows(asInteger: True)` | Passed | - |

Observed baseline failure first lines:

```text
System.InvalidOperationException : variable 'x' of type 'LiteDB.Tests.Issues.Issue2770_Tests+Row' referenced from scope '', but it is not defined
```


## #2867: Recognize inherited IDs named for the mapped derived type

- Frozen source: [LiteDB.Tests/Issues/Issue2867_Tests.cs](../../../LiteDB.Tests/Issues/Issue2867_Tests.cs); Git blob `a807d3189c9c78922e1bb9e377469bee3144dacb`.
- Class: `LiteDB.Tests.Issues.Issue2867_Tests`.
- Selection: `FullyQualifiedName~Issue2867_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Mapper/BsonMapper.GetEntityMapper.cs`, `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs`.
- Proposed minimum profile: Ubuntu .NET 8 and .NET 10; production build; compatibility for persisted mapping shape.

GetIdMember compares with the declaring type name. LINQ ResolveMember also asks for the declaring type mapper, so fixing member selection alone may leave inherited-property queries inconsistent with persisted _id. The controls pin ordinary Id precedence, directly declared type-name IDs and unrelated inherited suffixes. Both proposed files are in scope; only actually changed paths select the final profile.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Duplicate_inherited_mapped_type_ids_are_duplicate_primary_keys` | Failed | `f5104dc8a0a63a3dd450f73aa8a69a3777f1949bd756feeacff605b18393897d` |
| `Existing_id_precedence_and_suffix_boundaries_are_preserved` | Passed | - |
| `Inherited_mapped_type_id_is_the_persisted_key_and_query_path` | Failed | `c57efded2935a80c1df45c8d8396f2fc4327ff3953d67ec5a37ad5fd32bc05f3` |
| `Inherited_mapped_type_id_round_trips_through_the_id_field` | Failed | `7a7cb1b03d71842b21f88c2313e87fe22dcd2847e2436169ba8726b4fb06085b` |

Observed baseline failure first lines:

```text
Expected firstId.IsInt32 to be True, but found False.
```

```text
Expected a <LiteDB.LiteException> to be thrown, but no exception was thrown.
```

```text
Expected encoded.Keys[0] to be "_id" with a length of 3, but "Payload" has a length of 7, differs near "Pay" (index 0).
```


## #2845: Round-trip JSON doubles without losing bits or BSON type

- Frozen source: [LiteDB.Tests/Issues/Issue2845_Tests.cs](../../../LiteDB.Tests/Issues/Issue2845_Tests.cs); Git blob `dd180a83536a9ea537c99a606b6ae1e5d51bfcdc`.
- Class: `LiteDB.Tests.Issues.Issue2845_Tests`.
- Selection: `FullyQualifiedName~Issue2845_Tests`.
- Proposed exact production allowlist: `LiteDB/Document/Json/JsonWriter.cs`.
- Proposed minimum profile: Ubuntu .NET 8 and .NET 10; production build; compatibility and frozen LiteDB.Tests.Document.Bson_Tests coverage already included in broad.

The current 0.0######## double formatting loses precision, subnormals, small values and finite extrema. All eight cases use independent bit comparisons and repeated exports; 42 is the passing type-preservation control. The current serialization path rule already selects compatibility; add a reviewed explicit .NET 10 requirement for numeric-formatting behavior.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: -1.7976931348623157E+308)` | Failed | `8dfdacb4551a5f0500c82078b0ffc4eadc849b896831374034ae272ca91a1f99` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: -4.9406564584124654E-324)` | Failed | `def188a0c72036e38192effe93f069665fe4e39fe23a0c456490a85decc6eb3b` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 0.30000000000000004)` | Failed | `2b3ce80a7518bd70b26a33b4d8401543ce3fc83ba658929288ad4fb1cd2d62a7` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 1.2345678901234567)` | Failed | `fe7ced3fd980f9ee07ccdea9b860afb844a401d0ef51d54d113f1d1c2a63e6ef` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 1.7976931348623157E+308)` | Failed | `bef7d632750399f4a033cf1c03acb969a47251a596cafdca1960e8e3be71010a` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 1E-300)` | Failed | `c9f684c054eda2d365a959f61839f07279c5b3cc6bffcd52093a1cfb136c64a6` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 4.9406564584124654E-324)` | Failed | `9a230736449e961f60d27e63ae921ac7bcd6ee9853df5e15f1ee78a1e6b180fe` |
| `Json_preserves_double_type_and_bits_through_repeated_exports(value: 42)` | Passed | - |

Observed baseline failure first lines:

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be -4503599627370497L, but found -4503599627370496L (difference of 1).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be 4599075939470750516L, but found 4599075939470750515L (difference of -1).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be 9218868437227405311L, but found 9218868437227405312L (difference of 1).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be -9223372036854775807L, but found -9223372036854775808L (difference of -1).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be 4608238818662570491L, but found 4608238818662014491L (difference of -556000).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be 1L, but found 0L (difference of -1).
```

```text
Expected BitConverter.DoubleToInt64Bits(current["Value"].AsDouble) to be 118622047889322841L, but found 0L (difference of -118622047889322841).
```


## #2847: Respect or explicitly reject StringComparison translation

- Frozen source: [LiteDB.Tests/Issues/Issue2847_Tests.cs](../../../LiteDB.Tests/Issues/Issue2847_Tests.cs); Git blob `37965125a439f1864ef828448dc39e2571c2f56d`.
- Class: `LiteDB.Tests.Issues.Issue2847_Tests`.
- Selection: `FullyQualifiedName~Issue2847_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Mapper/Linq/TypeResolver/StringResolver.cs`.
- Proposed minimum profile: Ubuntu .NET 8 and Windows .NET 8; production build. No compatibility run for a translation-only change.

Equals translation drops the StringComparison argument. Both indexed and unindexed Ordinal cases fail under an ignore-case database collation; OrdinalIgnoreCase cases pass. The frozen tests explicitly permit intentional NotSupportedException for an unsupported mode. Passing therefore does not by itself establish full StringComparison support: decide and document supported behavior in the reviewed contract. Visitor changes, if needed, require scope expansion.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Explicit_string_comparison_agrees_with_CLR_even_with_an_index(comparison: Ordinal, indexed: False)` | Failed | `37526d01f07676f879d8ebd40a339b6673a5b40bb432618ef3ec307964863b66` |
| `Explicit_string_comparison_agrees_with_CLR_even_with_an_index(comparison: Ordinal, indexed: True)` | Failed | `37526d01f07676f879d8ebd40a339b6673a5b40bb432618ef3ec307964863b66` |
| `Explicit_string_comparison_agrees_with_CLR_even_with_an_index(comparison: OrdinalIgnoreCase, indexed: False)` | Passed | - |
| `Explicit_string_comparison_agrees_with_CLR_even_with_an_index(comparison: OrdinalIgnoreCase, indexed: True)` | Passed | - |

Observed baseline failure first lines:

```text
Expected col.Find(predicate).Select(x => x.Id).OrderBy(x => x) to be equal to {2}, but {1, 2, 3} contains 2 item(s) too many.
```


## #2779: Evaluate captured method chains without evaluating row expressions

- Frozen source: [LiteDB.Tests/Issues/Issue2779_Tests.cs](../../../LiteDB.Tests/Issues/Issue2779_Tests.cs); Git blob `3e6e8004572b8bca68fe15dd3230b4a3a775b3f3`.
- Class: `LiteDB.Tests.Issues.Issue2779_Tests`.
- Selection: `FullyQualifiedName~Issue2779_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs`.
- Proposed minimum profile: Ubuntu .NET 8 and .NET 10; production build. No compatibility run for a translation-only change.

Registered type resolvers intercept captured-only String and Enumerable calls before generic client evaluation. Preserve the boundary between captured expressions and row-dependent expressions, parameter reuse, changed captured values and the ID ledger. The two separate controls pin precomputed Contains and generic/resolver boundaries. Failure signatures below retain compiler-generated closure identities from the preserved artifacts rather than older notes. This file has a non-growing size exception.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Captured_method_calls_match_CLR_and_follow_changed_values(operation: "concat")` | Failed | `29cb9a5d102e1fe7a4d2cfa83fd9fa5a5c309faad2141a03cd27f04e99decb72` |
| `Captured_method_calls_match_CLR_and_follow_changed_values(operation: "distinct")` | Failed | `fb73107408c2e31512bd8663026791992018a5148cbd91871d3c2696f8203b17` |
| `Captured_method_calls_match_CLR_and_follow_changed_values(operation: "format")` | Failed | `db419470f244855cdf2d5f1f2dfc4f25f0068136dae26acd141c75d05be0d1a1` |
| `Captured_method_calls_match_CLR_and_follow_changed_values(operation: "join")` | Failed | `db419470f244855cdf2d5f1f2dfc4f25f0068136dae26acd141c75d05be0d1a1` |
| `Generic_client_evaluation_and_parameter_dependent_resolver_calls_keep_their_boundaries` | Passed | - |
| `Original_selectmany_distinct_contains_is_one_parameter_and_matches_id_ledger` | Failed | `133c043ce2eee2094e98ec922f927afe5bc1a5d30829b160197633ceede634ba` |
| `Precomputed_contains_control_uses_the_same_parameter_and_id_ledger` | Passed | - |

Observed baseline failure first lines:

```text
System.NotImplementedException : The method or operation is not implemented.
```

```text
System.NotSupportedException : Method Distinct() in Enumerable are not supported when convert to BsonExpression (value(LiteDB.Tests.Issues.Issue2779_Tests+<>c__DisplayClass3_0).allstocks.SelectMany(x => x.ContactIds).Distinct()).
```

```text
System.NotSupportedException : Method Concat(string,string,string) in String are not supported when convert to BsonExpression (Concat(value(LiteDB.Tests.Issues.Issue2779_Tests+<>c__DisplayClass6_0).part, ".", value(LiteDB.Tests.Issues.Issue2779_Tests+<>c__DisplayClass6_0).other)).
```

```text
System.NotSupportedException : Method Distinct() in Enumerable are not supported when convert to BsonExpression (value(LiteDB.Tests.Issues.Issue2779_Tests+<>c__DisplayClass6_0).names.Distinct()).
```


## #2205: Preserve complete oversized numeric-looking tokens as strings

- Frozen source: [LiteDB.Tests/Issues/Issue2205_Tests.cs](../../../LiteDB.Tests/Issues/Issue2205_Tests.cs); Git blob `f2c673cb6d7b0038213a4c6a0e835afb3762d74e`.
- Class: `LiteDB.Tests.Issues.Issue2205_Tests`.
- Selection: `FullyQualifiedName~Issue2205_Tests`.
- Proposed exact production allowlist: `LiteDB/Document/Expression/Parser/BsonExpressionParser.cs`.
- Proposed minimum profile: Ubuntu .NET 8; production build. No compatibility run for a parser-only change.

TryParseInt falls through to Int64.Parse and overflows. The frozen regression requires an oversized unquoted token to match the complete string value, with prefix/suffix distractors; merely replacing OverflowException with a friendlier syntax error will not satisfy it. This is a query-language semantic choice and needs explicit contract review before dispatch. Quoted and parameterized strings already pass. This file has a non-growing size exception.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Huge_numeric_looking_strings_are_preserved_when_quoted_or_parameterized` | Passed | - |
| `Out_of_range_unquoted_numeric_token_matches_the_complete_string_value` | Failed | `7e0ce48db5c04b9d0374f2f68aeedb4faf52749fb7a701734b100dee1215806e` |

Observed baseline failure first lines:

```text
System.OverflowException : Value was either too large or too small for an Int64.
```


## #2871: Synchronize constructor-cache hits with concurrent writes

- Frozen source: [LiteDB.Tests/Issues/Issue2871_Tests.cs](../../../LiteDB.Tests/Issues/Issue2871_Tests.cs); Git blob `85b265f26e6b995a7af450319bd53424c5959cbd`.
- Class: `LiteDB.Tests.Issues.Issue2871_Tests`.
- Selection: `FullyQualifiedName~Issue2871_Tests`.
- Proposed exact production allowlist: `LiteDB/Client/Mapper/Reflection/Reflection.cs`.
- Proposed minimum profile: Ubuntu and Windows, .NET 8 and .NET 10; production build. No compatibility run for a cache-only change.

The plain Dictionary read occurs outside the monitor used for writes. The deterministic lock test fails; a separate cache-hit/resize stress control passes. The regression permits a genuinely thread-safe replacement cache, so reviewers must inspect the synchronization guarantee as well as test results. Preserve the bounded timeouts and non-vacuous read/resize workload; a timeout is not a matching baseline defect.

| Exact case suffix | Required baseline outcome | Failure first-line SHA-256 |
| --- | --- | --- |
| `Cache_hits_remain_correct_while_unique_constructor_types_force_resizes` | Passed | - |
| `Plain_constructor_dictionary_serializes_cache_hit_reads` | Failed | `252103ca6e326c52e99fa00031825fd28b9951c10190c504a85d439f94fdc073` |

Observed baseline failure first lines:

```text
Expected hitCompletedWhileHeld to be False because a hit must use the dictionary monitor when production writes use that monitor, but found True.
```

## Dispatch blockers and later queue

- Add exact regression/control identities, first-line signatures, frozen blob IDs and approved production paths to a reviewed next-runtime manifest before registration. Preserve the existing #2874 contract. Bind fresh evidence to the actual integration base; these original reports cannot certify later bases.
- Add reviewed compact profile rules before expecting the proposed lane reductions. Compatibility is required for persisted mapping/key changes and JSON serialization. Do not classify every future issue in the same file as ordinary by filename alone.
- #2867, #2770 and #2779 can touch LinqExpressionVisitor.cs. Execute each against the latest accepted integration commit and revalidate the permanent passing ledger; do not merge stale independently prepared patches.
- #2847 needs an explicit decision on support versus deliberate rejection of unsupported StringComparison modes. #2205 needs explicit acceptance of the frozen unquoted-token-as-string behavior. #2871 needs a synchronization review even if a concurrent replacement takes the allowed alternate test branch.
- Proposed source scopes are bounded hypotheses from the original code. A worker that needs another file must request a reviewed scope/profile update; a green target alone cannot authorize extra paths. Preserve non-growing size limits for LiteQueryable.cs, LinqExpressionVisitor.cs and BsonExpressionParser.cs.
- #2860 remains deferred because its frozen class also contains two distinct maximum-depth diagnostic failures in addition to URI cases; dropping those cases would hide separate defects. Split the execution contract only after explicit case-level disposition.
- #2870 remains deferred until environment-dependent stack/frame evidence has a reviewed signature contract. #2769 needs broader integer-enum/UInt64 serialization and index compatibility review; #2093 changes missing-value/default query semantics beyond a narrow mapper fix.
- #2814 remains explicitly intermittent. A pass or failure change is inconclusive until repeat controls resolve it; do not auto-accept it through an unrelated fix.

The queue covers ten additional deterministic reproduced reports, not the complete open-bug inventory. Continue inventory disposition after this wave, retaining explicit reasons and required evidence for deferred reports.
