# Third bounded preparation wave

Prepared from automation commit `d020aceb92799d9325a2f47669d1d39e5d700248`. This prepares a later immutable runtime; the live v7 campaign and existing thirteen contracts are unchanged. No source implementation, frozen test modification, signature normalization or remote action is included.

## Decision

Eight reports are prepared with **40 exact failing cases and 14 positive controls**. #2860 and #2870 are deferred for environment-aware contract design. Every selected case and first-line signature, including neighboring controls, matches all six preserved original baseline reports from run [34988724926](https://github.com/litedb-org/LiteDB/actions/runs/34988724926).

[wave-three-baseline.json](../../../scripts/bugfix/fixtures/wave-three-baseline.json) retains exact identities, outcome/first-line values, every test source blob, selected filters, required environments, numeric artifact IDs and SHA-256 pins. It also retains the deferred issues' original reference cases and actual per-lane differences. Each local raw TRX was checked against its previously independently verified artifact hash. Original tests and source are pinned to `dd937719f7eee53c512f50ac604cab639bf42a4c`.

The manifest adds these eight definitions without changing the previous thirteen. Planner policy `compressed-acceptance-v4` adds only reviewed issue/path pairs. Every profile runs focused plus broad and a production build; compatibility checks are retained for storage, persisted numeric/mapping changes and SQL rebuild/commit paths. Broad already executes relevant targeted classes, so do not rerun them separately. Unknown additional paths require approved scope expansion and still select the conservative six-lane compatibility fallback. Full matrix remains reserved for the end of the entire sweep.

## Selected contracts

| Issue | Baseline red / controls | Required environments | Compatibility |
| --- | --- | --- | --- |
| #1159 | 1 / 1 | linux-x64-net8.0 | No |
| #1224 | 11 / 1 | linux-x64-net8.0, linux-x64-net10.0 | Yes |
| #2858 | 4 / 1 | linux-x64-net8.0, linux-x64-net10.0, windows-x64-net8.0, windows-x64-net10.0 | Yes |
| #2864 | 12 / 2 | linux-x64-net8.0 | Yes |
| #2769 | 7 / 5 | linux-x64-net8.0, linux-x64-net10.0 | Yes |
| #2225 | 1 / 2 | linux-x64-net8.0, linux-x64-net10.0, windows-x64-net8.0, windows-x64-net10.0 | Yes |
| #2322 | 2 / 1 | linux-x64-net8.0, linux-x64-net10.0 | No |
| #2873 | 2 / 1 | linux-x64-net8.0, linux-x64-net10.0 | Yes |

## #1159

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue1159_|FullyQualifiedName=LiteDB.Tests.Mapper.CustomMappingCtor_Tests.Custom_Ctor_With_Custom_Id`.
- Allowed production paths: `LiteDB/Client/Mapper/EntityBuilder.cs`.

Repeated Ignore currently removes the member and the second GetMember lookup throws. The separate Custom_Ctor_With_Custom_Id control configures EntityBuilder.Id through that same lookup helper and verifies constructor identity and the other field. Scope stays in EntityBuilder.cs; no broad ignore/missing-member suppression is authorized.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue1159_Tests.cs](../../../LiteDB.Tests/Issues/Issue1159_Tests.cs): `80fa2f0525c2d0507de042bb69af5a7677363892`.
- [LiteDB.Tests/Mapper/CustomMapping_Tests.cs](../../../LiteDB.Tests/Mapper/CustomMapping_Tests.cs): `50b42d032c84eea7ad4acf7c00da77ec2dfe745f`.

Exact required positive controls:

- `LiteDB.Tests.Mapper.CustomMappingCtor_Tests.Custom_Ctor_With_Custom_Id`.

Role-specific task instructions:

- **behavior**: Make repeated Ignore idempotent while preserving valid Id, Field and DbRef member selection. Do not silently accept invalid member expressions or remove unrelated fields. The independent custom-ID constructor control exercises the shared GetMember helper.

## #1224

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue1224_|FullyQualifiedName=LiteDB.Tests.Issues.Issue2869_Tests.Native_numeric_conversions_work_and_non_numeric_values_are_not_coerced`.
- Allowed production paths: `LiteDB/Document/BsonValue.cs`.

Both implicit UInt64 directions and typed indexed reopen fail. The separate #2869 control already proves native numeric conversions and rejection of non-numeric coercion. Freeze its original file and keep this accepted neighboring behavior permanently passing. Serialization of typed UInt64 already uses signed bits; the current implicit BsonValue operators lose or reject them.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue1224_Tests.cs](../../../LiteDB.Tests/Issues/Issue1224_Tests.cs): `5b88c28910e569bb635d8e08af0da9a5e2478375`.
- [LiteDB.Tests/Issues/Issue2869_Tests.cs](../../../LiteDB.Tests/Issues/Issue2869_Tests.cs): `75f14d75945fe6912b007b9698e8d38898b02413`.

Exact required positive controls:

- `LiteDB.Tests.Issues.Issue2869_Tests.Native_numeric_conversions_work_and_non_numeric_values_are_not_coerced`.

Role-specific task instructions:

- **behavior**: Preserve every UInt64 bit through Int64 BSON, including UInt64.MaxValue and the upper half. Keep native numeric conversions and non-numeric rejection. Review indexed equality, Id lookups and the accepted #2869 implicit widening fix.
- **compatibility**: Compare old persisted BSON Double values from implicit ulong conversions with the new Int64 representation, including ulong values that fit Int64. Examine round-trip, index, equality and ordering effects for old and new documents. Inspect existing callers converting those BsonValues to double and interaction with #2869; satisfying IsInt64 alone is insufficient. Report legacy Double precision loss and evidence or limitations.

## #2858

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2858_|FullyQualifiedName=LiteDB.Tests.Engine.Index_Tests.Index_With_Like`.
- Allowed production paths: `LiteDB/Client/SqlParser/SqlParser.cs`, `LiteDB/Client/SqlParser/Commands/Insert.cs`.

The independent Index_With_Like control executes uppercase SELECT/FROM/WHERE/LIKE through LiteDatabase.Execute and validates indexed mixed-case literal results. It passes all six baseline lanes. It is not an API-only insert/rollback substitute. The frozen issue tests also execute their uppercase insert/rebuild/commit paths before corresponding lowercase failures, then check persisted state when execution succeeds. Two parser dispatch files use culture-sensitive ToUpper; no global culture mutation or storage rewrite is authorized.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2858_Tests.cs](../../../LiteDB.Tests/Issues/Issue2858_Tests.cs): `f7ac80752a4210eaf5b64d393d3af5d3aaa3e80f`.
- [LiteDB.Tests/Engine/Index_Tests.cs](../../../LiteDB.Tests/Engine/Index_Tests.cs): `16c522c1c4086897a294660c04082e73899a5fb0`.

Exact required positive controls:

- `LiteDB.Tests.Engine.Index_Tests.Index_With_Like`.

Role-specific task instructions:

- **behavior**: Make SQL keyword and auto-ID dispatch culture-invariant without changing data literals or global CurrentCulture. Preserve uppercase SQL and Turkish lowercase insert, auto-ID, rebuild and commit semantics across NLS/ICU and required runtimes. The separate Index_With_Like control calls LiteDatabase.Execute through the affected SQL parser and checks mixed-case literal results.
- **compatibility**: Verify rebuilt files preserve requested collation, unique indexes, UserVersion and data; verify lowercase commit durably closes the transaction before reopen. Generic format compatibility does not establish SQL rebuild/commit semantics; inspect the frozen persisted-state checks.
- **lifecycle**: Preserve transaction ownership and culture restoration. Do not replace parser invariance with global culture mutation or bypass rebuild/commit.

## #2864

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2864_`.
- Allowed production paths: `LiteDB/Engine/Pages/CollectionPage.cs`, `LiteDB/Engine/Pages/IndexPage.cs`.

The two page constructors validate stored PageType but omit its actual value in the error. Twelve exact mismatch cases include valid alternative enum types and unknown byte 127; two matching-page controls validate unchanged header bytes and normal acceptance. The general storage path rule retains compatibility despite the diagnostic-only intended change.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2864_Tests.cs](../../../LiteDB.Tests/Issues/Issue2864_Tests.cs): `4e2ec45ea4d0dbd3a82634b5c980986918fb964e`.

Exact required positive controls:

- `LiteDB.Tests.Issues.Issue2864_Tests.Matching_page_type_is_accepted_and_preserves_the_raw_header(rawType: 2)`.
- `LiteDB.Tests.Issues.Issue2864_Tests.Matching_page_type_is_accepted_and_preserves_the_raw_header(rawType: 3)`.

Role-specific task instructions:

- **behavior**: Report the actual stored page type including unknown byte 127; preserve INVALID_DATAFILE_STATE and the existing validation boundary. Never accept a mismatched page or substitute a valid enum type.
- **compatibility**: Preserve valid page headers and persisted bytes. The matching-page controls must preserve the raw header; diagnostic changes must not alter storage acceptance or file-format semantics.

## #2769

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2769_`.
- Allowed production paths: `LiteDB/Client/Mapper/BsonMapper.Serialize.cs`, `LiteDB/Client/Mapper/BsonMapper.Deserialize.cs`.

Seven cases overflow Int32 while five small backing-type controls pass. Preserve independent signed-bit BSON decoding, UInt64 upper-half distinctions, indexed equality and reopen. Both serialization and deserialization paths are in scope; do not silently expand into unrelated numeric or index code. Run after #1224 and the existing mapper wave against the latest integration base.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2769_Tests.cs](../../../LiteDB.Tests/Issues/Issue2769_Tests.cs): `a7b1da15a2d52d1a32195efb326d0d4919678652`.

Exact required positive controls:

- `LiteDB.Tests.Issues.Issue2769_Tests.Every_enum_backing_type_preserves_its_integer_value(value: Value, expected: -127)`.
- `LiteDB.Tests.Issues.Issue2769_Tests.Every_enum_backing_type_preserves_its_integer_value(value: Value, expected: -2000000000)`.
- `LiteDB.Tests.Issues.Issue2769_Tests.Every_enum_backing_type_preserves_its_integer_value(value: Value, expected: -32000)`.
- `LiteDB.Tests.Issues.Issue2769_Tests.Every_enum_backing_type_preserves_its_integer_value(value: Value, expected: 254)`.
- `LiteDB.Tests.Issues.Issue2769_Tests.Every_enum_backing_type_preserves_its_integer_value(value: Value, expected: 65000)`.

Role-specific task instructions:

- **behavior**: Preserve all signed and unsigned enum backing types, including UInt64 high bits and distinct values, without Double conversion or Int32 truncation. Keep independently authored BSON decoding, typed indexed equality and all five small-integer controls. Review neighboring #1224 UInt64 behavior.
- **compatibility**: Assess existing EnumAsInteger Int32 documents alongside new Int64 values and EnumAsInteger=false string data. Preserve enum identity, index lookup and reopen. Explain signed-bit representation of upper-half UInt64 and ordering effects; generic file-format compatibility is insufficient.

## #2225

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2225_|FullyQualifiedName=LiteDB.Tests.Mapper.CustomMapping_Tests.Custom_Ctor|FullyQualifiedName=LiteDB.Tests.Mapper.CustomMapping_Tests.ParameterLess_Ctor`.
- Allowed production paths: `LiteDB/Client/Mapper/Reflection/Reflection.Expression.cs`.

The regression uses a fixed Guid.Parse literal, so its exact failure is stable. Existing Custom_Ctor and ParameterLess_Ctor controls separately prove immutable constructor state and ordinary setters using independent documents. The inherited private setter is missed by the CanWrite check in Reflection.Expression.cs; required Windows/Linux net8/net10 checks cover expression/reflection behavior. Read-only properties must remain read-only.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2225_Tests.cs](../../../LiteDB.Tests/Issues/Issue2225_Tests.cs): `a9ac8b9776b901b060e1c850b4c87cde2726acf2`.
- [LiteDB.Tests/Mapper/CustomMappingCtor_Tests.cs](../../../LiteDB.Tests/Mapper/CustomMappingCtor_Tests.cs): `3695c48e8d999db66b4c5a1dd89a447da750baee`.

Exact required positive controls:

- `LiteDB.Tests.Mapper.CustomMapping_Tests.Custom_Ctor`.
- `LiteDB.Tests.Mapper.CustomMapping_Tests.ParameterLess_Ctor`.

Role-specific task instructions:

- **behavior**: Restore inherited private Id setters using the correct declaring member while preserving ordinary parameterless and constructor-only mapping. The fixed Guid must survive reopen, update and FindById without inserting another record.
- **compatibility**: Inspect existing inherited-ID documents, read-only properties and constructor mapping. Do not broadly bypass setters or expand non-public access. Compare expression/reflection behavior on required Windows/Linux runtimes and state evidence or limitations.

## #2322

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2322_|FullyQualifiedName=LiteDB.Tests.Mapper.Enum_Tests.Enum_Convert_Into_Linq_Query`.
- Allowed production paths: `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs`.

Both generic enum equality modes fail. The independent Enum_Convert_Into_Linq_Query control pins ordinary integer and nullable enum translation. This shares LinqExpressionVisitor.cs with #2770/#2779: rebase sequentially and stop for explicit disposition if those fixes already make it pass.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2322_Tests.cs](../../../LiteDB.Tests/Issues/Issue2322_Tests.cs): `96f85eb02a876deaf1d9c663948de0dfa5c3daa5`.
- [LiteDB.Tests/Mapper/Enum_Tests.cs](../../../LiteDB.Tests/Mapper/Enum_Tests.cs): `4d4fbab81ad8ba9060c7da5bffc451a6ea8e54c0`.

Exact required positive controls:

- `LiteDB.Tests.Mapper.Enum_Tests.Enum_Convert_Into_Linq_Query`.

Role-specific task instructions:

- **behavior**: Support generic enum Equals through IRow<K> in both integer and string storage modes, including unknown enum values and nonmatching rows. Preserve ordinary enum LINQ and row-dependent/client-evaluation boundaries. Review prior #2770/#2779 changes; report an already-green issue instead of manufacturing a baseline failure.

## #2873

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2873_`.
- Allowed production paths: `LiteDB/Client/Mapper/EntityBuilder.cs`, `LiteDB/Client/Mapper/EntityMapper.cs`, `LiteDB/Client/Mapper/BsonMapper.Deserialize.cs`.

The whole issue includes TWO frozen classes: the explicit API/legacy compatibility class and the reported System.Enum model. Both regressions must pass; the old Ctor(factory) post-population control must remain green. EntityBuilder, EntityMapper and BsonMapper.Deserialize are the narrow opt-in path. Existing v7 changes to virtual mapping and enum decoding must be reviewed before dispatch.

Frozen source blobs:

- [LiteDB.Tests/Issues/Issue2873_Tests.cs](../../../LiteDB.Tests/Issues/Issue2873_Tests.cs): `6112d1742227cb16cdb28cb55e89b3e4a316cbc8`.
- [LiteDB.Tests/Issues/Issue2873_ReportedModelTests.cs](../../../LiteDB.Tests/Issues/Issue2873_ReportedModelTests.cs): `f354acc80bf3df2a1c2407df424719d729b3c5d5`.

Exact required positive controls:

- `LiteDB.Tests.Issues.Issue2873_Tests.Existing_ctor_mapping_keeps_populating_mapped_members_for_compatibility`.

Role-specific task instructions:

- **behavior**: Provide explicit CtorOnly(factory) or Ctor(factory, populateMembers: false) opt-in. Preserve constructor-owned normalized/validated state and factory result identity without running mapped setters. Include the separately frozen reported System.Enum model and independent reopen test; exposing a method alone is insufficient.
- **compatibility**: The existing one-argument Ctor(factory) must keep post-construction member population. Preserve old documents, init-only members, DbRef and custom DeserializeObject behavior; assess prior #2802 virtual-mapping changes and legacy constructor callers.
- **lifecycle**: Preserve mapper initialization, entity configuration isolation, factory invocation count and concurrent readers. Do not globally disable member population or swallow factory failures.

## Deferred reports

### #2860

The frozen selection mixes six relative-URI failures, six absolute-URI controls and two maximum-depth diagnostic failures on Linux/macOS. The rooted `/a/b` URI cases fail on every lane: Windows throws UriFormatException, while Linux/macOS reaches the IsAbsoluteUri assertion with an absolute URI. Registering one common exact signature or dropping those cases would lose evidence. The frozen source blob and all differences remain in the fixture. Required next step: explicit environment-aware case contract and decision whether URI and diagnostic failures are one approved scope. No new normalization or partial issue filter is introduced.

### #2870

Four stack-preservation assertions embed checkout paths and source frames; exact first lines differ across runner environments and can change with the integration source. Three healthy controls pass. The frozen source blob and all observed lane differences remain in the fixture. Required next step: reviewed environment-aware evidence design that still proves original stack origin; this preparation does not normalize or weaken any stack assertion.

## Dispatch and overlap boundaries

- Keep all existing thirteen definitions unchanged. Review and promote this preparation at a new immutable runtime; old profiles cannot be reused because the policy/contract hashes change.
- Run each fresh baseline on the latest integration commit. Same-file work is sequential: #1159/#2873 share EntityBuilder; #2769/#2873 share deserialization; #2225 touches the expression companion of #2871 reflection; #2322 follows #2770/#2779; #1224 follows #2869. Related tests becoming green require explicit disposition, not manufactured failures or blanket acceptance.
- Each issue carries review_requirements in task.json, including numeric type/API compatibility, old persisted data, constructor behavior, reflection boundaries and SQL durability. Those reviews must cite code or executed evidence and report limitations; generic compatibility checks are not substitutes for those contracts.
- Extra neighboring controls are original passing methods with exact OR filters and frozen source blobs. No test extraction, rewriting or synthetic green control is allowed.
- #2873 must keep the reported-model class in its focused selection, and #1224 must assess existing callers converting implicitly constructed ulong BsonValues to double.

Validation includes exact baseline gate checks against all six preserved raw reports, original blob/scope validation, unchanged-existing-contract tests, focused failure/control tests, compact-profile checks and task-contract role-note/hash checks. No new CI was launched.
