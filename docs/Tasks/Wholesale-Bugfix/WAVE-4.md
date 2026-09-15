# Fourth bounded preparation wave

Prepared from `61ee4aec705024c189345de25508ea19ffc2584e` for a later immutable runtime. The existing twenty-one contracts remain unchanged. No implementation, frozen-test change, normalization change, source-guard allowance or remote action is included.

## Evidence and decision

Four contracts select **19 red cases and 13 genuine passing controls**. All 32 exact identities, outcomes and first-line signatures match every preserved original baseline lane (24 issue/lane gate checks). Evidence is from the independent baseline side of run [34988724926](https://github.com/litedb-org/LiteDB/actions/runs/34988724926), not its enclosing candidate acceptance result. Both baseline source and test revision are `dd937719f7eee53c512f50ac604cab639bf42a4c`; workflow SHA is `7629236e73bde318e934509244c3e61ef820d8db`. Each execution.json identity and raw TRX hash was checked locally.

[wave-four-baseline.json](../../../scripts/bugfix/fixtures/wave-four-baseline.json) records exact case names, first-line values, source blobs, selected filters and independent artifact IDs. No exception normalization was introduced. A fresh focused baseline on current integration is still required before dispatch; historical evidence does not authorize credit for an already passing case.

| Original baseline artifact | ID | Raw TRX SHA-256 |
| --- | --- | --- |
| bugfix-check-ubuntu-latest-net8.0 | 10405370121 | `a4d3b367c661fa8768fbb8668d4a8827713618fa3b488f15f02ca1607c8bd56b` |
| bugfix-check-ubuntu-latest-net10.0 | 10405505060 | `d4d7854e6d5e036da9d1ad92ac4f5aebe0a23b8033fe5a50f9793dcf18cbe423` |
| bugfix-check-windows-latest-net8.0 | 10404073794 | `931cbd570f47bf6a376f11dde76c0ecf1399f9d742acf0f90eeb9463d781bdf0` |
| bugfix-check-windows-latest-net10.0 | 10404574219 | `789212a8598feb82270173bbc4b6e152682e4b1177a51ab6f7ad67d1dcc0c08e` |
| bugfix-check-macos-latest-net8.0 | 10405111338 | `c069b53a2512adcd39c9ecf0bbf1d1329ff1371fa21228dbf4de3923340ee719` |
| bugfix-check-macos-latest-net10.0 | 10405450249 | `487ac255e77785b858569e7dbbd93a1e5422db470aae03c8c0d71741709b4e08` |

## Compact profiles

Every profile runs focused plus broad and a production build. Relevant targeted tests already execute inside broad; no duplicate test run is needed. Full matrix remains the final sweep gate. Planner policy `compressed-acceptance-v5` adds exact reviewed #2807/#1829 ordinary pairs and an ObjectId compatibility pair for #1444. #2746 keeps the existing storage rule. Unapproved paths fail scope validation; reviewed unknown scope still receives conservative six-lane compatibility fallback.

| Issue | Red / controls | Required lanes | Compatibility |
| --- | --- | --- | --- |
| #2746 | 1 / 3 | Ubuntu net8 | Yes, existing storage rule |
| #2807 | 11 / 2 | Ubuntu net8 | No |
| #1829 | 3 / 3 | Ubuntu net8 + net10 | No |
| #1444 | 4 / 5 | Ubuntu net8 + net10 | Yes, explicit ObjectId rule |

## #2746

The three separately passing theory cases cover invalid initial digits, valid later digits, exact persisted values and recovery after a rejected write. The regression checks committed data and reopen before reaching the diagnostic assertion. Keep scope in CollectionService.cs; changing the accepted grammar is not a fix.

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2746_`.
- Production scope: `LiteDB/Engine/Services/CollectionService.cs`.

Frozen source blobs:

- `LiteDB.Tests/Issues/Issue2746_Tests.cs`: `3b4d3213b45bb1fd1a2ab9157f2a3463c016da42`.

Exact cases (R = required baseline failure; C = required passing control):

- **C** `LiteDB.Tests.Issues.Issue2746_Tests.Collection_name_contract_distinguishes_initial_digits_from_later_digits(name: "2e4e2069_da7c_4dbd_8c11_a5ac31041079", valid: False)`
- **C** `LiteDB.Tests.Issues.Issue2746_Tests.Collection_name_contract_distinguishes_initial_digits_from_later_digits(name: "_2", valid: True)`
- **C** `LiteDB.Tests.Issues.Issue2746_Tests.Collection_name_contract_distinguishes_initial_digits_from_later_digits(name: "a2e4e2069_da7c_4dbd_8c11_a5ac31041079", valid: True)`
- **R** `LiteDB.Tests.Issues.Issue2746_Tests.Initial_digit_error_explains_the_position_rule_and_preserves_committed_data`
  - First-line SHA-256: `8167c68e9f180c3d96a0558c46b82a72331051b75840d435081410fdae85a853`; verbatim value in evidence JSON.

Role requirements delivered in task.json:

- **behavior**: Preserve the collection-name grammar, INVALID_COLLECTION_NAME error code and rejected-write boundary. Explain the initial-digit restriction without accepting previously invalid names or rejecting valid later digits.
- **compatibility**: Preserve committed documents and subsequent valid writes after invalid collection creation, including checkpoint and reopen. Do not change file format or collection naming acceptance to satisfy the diagnostic.

## #2807

The issue class has eleven failures and no green cases. QueryApi_Tests.Query_And and Query_StartsWith are independent executed controls: they call collection.Find with the same helper API and compare result counts and each row to in-memory LINQ through AssertEx.ArrayEqual. Query_And covers typed integer/boolean composition; Query_StartsWith covers ordinary prefix results. Each issue theory checks shared expression source, nonempty parameters and first/second/first execution against independent expected values.

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue2807_|FullyQualifiedName=LiteDB.Tests.QueryTest.QueryApi_Tests.Query_And|FullyQualifiedName=LiteDB.Tests.QueryTest.QueryApi_Tests.Query_StartsWith`.
- Production scope: `LiteDB/Client/Structures/Query.cs`.

Frozen source blobs:

- `LiteDB.Tests/Issues/Issue2807_Tests.cs`: `bf5e8414ada777029c93c93ee07bbd2c0908e754`.
- `LiteDB.Tests/Query/QueryApi_Tests.cs`: `9cef4afa20be0ef4403567e86e4e42c235a2ae26`.

Exact cases (R = required baseline failure; C = required passing control):

- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "Between")`
  - First-line SHA-256: `3b77c553c48ae0cc366b79f298fe5ec7952883ad4227f4e1b4f6270203f0355c`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "Contains")`
  - First-line SHA-256: `60c2986ddd5e58d94b999b77839ddcec0ca90804de69c969a479e27e04064537`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "EQ")`
  - First-line SHA-256: `4e19185d4a3cbf9f5380c5d74f58b3ebd13bf3ae11c87aef67c17c02ac1ad62f`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "EndsWith")`
  - First-line SHA-256: `43c8cf3914049ed870d3ae4da639a307a1a57a8610cb8fba42c110a7753d9be2`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "GT")`
  - First-line SHA-256: `439db4f24189a01a0f8b0815025b0e5e8dd926b6448801bae07b40cbf8d660fa`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "GTE")`
  - First-line SHA-256: `c4de81bbce02f5035df2546bb7dbb9595e7924950f5bc060e388af6027128463`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "In")`
  - First-line SHA-256: `6de5cbfc5b6ea5000ff169f26478b251e59de59e5574c96169af1dda97def14a`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "LT")`
  - First-line SHA-256: `91a6094863cc03b1a4c6676cad0c9a50bb7b241a820a758f43d794c55222ab13`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "LTE")`
  - First-line SHA-256: `32547e4b82f31102ce3f2db20d94a356bd5ea15d55e853d2ad396a206cf9034a`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "Not")`
  - First-line SHA-256: `b76bcb1b268dde29f46df0cda6d8e95a74ab38ffa95d070f0399d322458b03f2`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue2807_Tests.Helper_reuses_source_but_keeps_independent_parameter_values(operation: "StartsWith")`
  - First-line SHA-256: `4af5005626b187df524d36011f578058c067822885f59c7fd2e84b9bf89777e0`; verbatim value in evidence JSON.
- **C** `LiteDB.Tests.QueryTest.QueryApi_Tests.Query_And`
- **C** `LiteDB.Tests.QueryTest.QueryApi_Tests.Query_StartsWith`

Role requirements delivered in task.json:

- **behavior**: Parameterize all eleven Query helpers while preserving independent values, operation boundaries and existing LIKE wildcard/literal semantics. Compare Query.And/Or/Not composition and parameter-name collisions; equal expression source must not cause different queries to share values. The separate Query_And and Query_StartsWith controls execute actual database queries against in-memory expected results.
- **lifecycle**: Review repeated and concurrent query use, cache reuse and parameter ownership. Do not introduce a shared mutable parameter dictionary or retain values from a previous expression.

## #1829

Three opposite-operand cases pass independently. Every case validates compiled CLR truth tables and exact IDs, Count and enumeration. Nested outer and inner parameters deliberately share the name p but have distinct identities. Failures occur in Count (expected 2/3, actual 0/1), so merely preserving enumeration is insufficient. Fresh integration baselines and accepted-ledger checks must cover shared visitor changes from #2770/#2779/#2322.

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue1829_`.
- Production scope: `LiteDB/Client/Mapper/Linq/LinqExpressionVisitor.cs`.

Frozen source blobs:

- `LiteDB.Tests/Issues/Issue1829_Tests.cs`: `dddd92292528613e080fa8eb777f2406267e8c2c`.

Exact cases (R = required baseline failure; C = required passing control):

- **C** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_keeps_matches_from_each_independent_predicate(idPredicateFirst: False)`
- **R** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_keeps_matches_from_each_independent_predicate(idPredicateFirst: True)`
  - First-line SHA-256: `81e572447cdaa4ccf154c1fa5fdff8d3a1a9962852bf4a2830e82c30884058c5`; verbatim value in evidence JSON.
- **C** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_matches_direct_linq_in_both_operand_orders(falseFirst: False)`
- **R** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_matches_direct_linq_in_both_operand_orders(falseFirst: True)`
  - First-line SHA-256: `e8290d21048081a1c6fb2f321dfe45fbcab1ba7a277daab43aa5c5b1d772d1d7`; verbatim value in evidence JSON.
- **C** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_preserves_same_named_nested_parameter_scopes(falseFirst: False)`
- **R** `LiteDB.Tests.Issues.Issue1829_Tests.Predicate_builder_or_preserves_same_named_nested_parameter_scopes(falseFirst: True)`
  - First-line SHA-256: `e8290d21048081a1c6fb2f321dfe45fbcab1ba7a277daab43aa5c5b1d772d1d7`; verbatim value in evidence JSON.

Role requirements delivered in task.json:

- **behavior**: Preserve invocation argument binding by parameter identity, not parameter name. Both operand orders, independent predicates and same-named nested scopes must return the hard-coded IDs, with Count equal to enumeration. Do not rewrite the frozen PredicateBuilder helper or coerce all nested parameters into the root scope. Review interaction with #2770/#2779/#2322 in LinqExpressionVisitor and keep their accepted cases passing.

## #1444

Five green vectors cover the epoch, last signed timestamp, within-half ordering and remaining-byte tie breaks. Four failures cover unsigned CreationTime, crossing the sign boundary and primary-index order. The test named Primary_index_round_trips_and_orders_the_full_unsigned_timestamp_range creates a fresh in-memory database; it does not reopen a legacy database. AsUnsignedTimestamp normalizes the public value for assertion and therefore does not protect Timestamp ABI: explicit compatibility review is mandatory. #2874 already modifies this same production file.

- Exact filter: `FullyQualifiedName~LiteDB.Tests.Issues.Issue1444_`.
- Production scope: `LiteDB/Document/ObjectId.cs`.

Frozen source blobs:

- `LiteDB.Tests/Issues/Issue1444_Tests.cs`: `322c10d4a4c2aa51bafb0fd6a18f0bb7fe227a5f`.

Exact cases (R = required baseline failure; C = required passing control):

- **C** `LiteDB.Tests.Issues.Issue1444_Tests.Comparison_follows_the_unsigned_timestamp_then_the_remaining_bytes(earlierHex: "00000000ffffffffffffffff", laterHex: "7fffffff0000000000000000")`
- **R** `LiteDB.Tests.Issues.Issue1444_Tests.Comparison_follows_the_unsigned_timestamp_then_the_remaining_bytes(earlierHex: "7fffffffffffffffffffffff", laterHex: "800000000000000000000000")`
  - First-line SHA-256: `13d5922db1b07693c100ede4ce61798eef64db21f6b7cce214c67995ff069441`; verbatim value in evidence JSON.
- **C** `LiteDB.Tests.Issues.Issue1444_Tests.Comparison_follows_the_unsigned_timestamp_then_the_remaining_bytes(earlierHex: "800000000000000000000000", laterHex: "800000000000000000000001")`
- **C** `LiteDB.Tests.Issues.Issue1444_Tests.Comparison_follows_the_unsigned_timestamp_then_the_remaining_bytes(earlierHex: "80000000ffffffffffffffff", laterHex: "ffffffff0000000000000000")`
- **R** `LiteDB.Tests.Issues.Issue1444_Tests.Primary_index_round_trips_and_orders_the_full_unsigned_timestamp_range`
  - First-line SHA-256: `bb8084fb436ca9c01b4ae406ab83c1c1c33944b7eabee8d52b08c4775feb2e52`; verbatim value in evidence JSON.
- **C** `LiteDB.Tests.Issues.Issue1444_Tests.Timestamp_bits_round_trip_and_creation_time_uses_unsigned_seconds(expectedBytes: [0, 0, 0, 0, 17, ···], expectedHex: "000000001122334455667788", expectedTimestamp: 0, expectedCreationTime: 1970-01-01T00:00:00.0000000Z)`
- **C** `LiteDB.Tests.Issues.Issue1444_Tests.Timestamp_bits_round_trip_and_creation_time_uses_unsigned_seconds(expectedBytes: [127, 255, 255, 255, 17, ···], expectedHex: "7fffffff1122334455667788", expectedTimestamp: 2147483647, expectedCreationTime: 2038-01-19T03:14:07.0000000Z)`
- **R** `LiteDB.Tests.Issues.Issue1444_Tests.Timestamp_bits_round_trip_and_creation_time_uses_unsigned_seconds(expectedBytes: [128, 0, 0, 0, 17, ···], expectedHex: "800000001122334455667788", expectedTimestamp: 2147483648, expectedCreationTime: 2038-01-19T03:14:08.0000000Z)`
  - First-line SHA-256: `fff9e8e3f8059153e11395613f60b799565400335a7e4c5c751a6dc21592c5b2`; verbatim value in evidence JSON.
- **R** `LiteDB.Tests.Issues.Issue1444_Tests.Timestamp_bits_round_trip_and_creation_time_uses_unsigned_seconds(expectedBytes: [255, 255, 255, 255, 17, ···], expectedHex: "ffffffff1122334455667788", expectedTimestamp: 4294967295, expectedCreationTime: 2106-02-07T06:28:15.0000000Z)`
  - First-line SHA-256: `3ea08a7608d3767df09790429827b312c282e7618393a1fd1217e924f899fcfb`; verbatim value in evidence JSON.

Role requirements delivered in task.json:

- **behavior**: Interpret the ObjectId timestamp bits as unsigned seconds for CreationTime and ordering while preserving the remaining-byte tie break, equality, JSON and 12-byte wire representation. Preserve accepted #2874 constructor window validation.
- **compatibility**: Examine old ObjectId indexes written with signed timestamp ordering, especially mixed pre/post-2038 keys: lookup, range traversal and insert behavior under a new comparer may require rebuild or migration. The frozen primary-index test creates a fresh database and does not prove old indexes are compatible; generic vector compatibility does not exercise this ordering. Supply evidence and explicitly report unresolved compatibility limitations. Preserve the public Timestamp API type/ABI and raw timestamp bits unless an independently reviewed API change is authorized.

## Held candidates and source-guard surprises

These are evidence only, without execution contracts or guard exemptions. Raw case inventories and runtime differences are retained in the evidence JSON. Source contexts were inspected against original tests and current integration `781b3291`; independently review any observation disposition before execution.

| Issue | Original red / own controls | Narrow prospective scope | Blocker |
| --- | --- | --- | --- |
| #2801 | 11 / 1 | Document/BsonValue.cs | Source guard 110; frozen cases permit descriptive rejection instead of support |
| #2767 | 3 / 0 | Engine/Disk/DiskService.cs | Source guard 159; normal-stream neighboring control needs separate contract |
| #2819 | 12 / 4 | Engine/Disk/Serializer/BufferReader.cs and Engine/Sort/SortContainer.cs | Source guards 38/149; persisted index key format and oversize validation |
| #2367 | 3 / 0 | Client/Mapper/Linq/LinqExpressionVisitor.cs | Runtime-specific reflection failures; environment-aware schema |

#2801 has a real scalar/ordinary object control, but most collection cases permit a descriptive ArgumentException recommending BsonArray/BsonDocument/BsonMapper. Reviewers must disclose support loss rather than claim mutable collection support. It overlaps #2869 and #1224. A future profile should retain runtime and persisted BSON compatibility checks.

#2767 failures are exact short-read byte counts 1, 257 and 4095. Healthy_FileReaderV8_reopens_persisted_documents from frozen #2870 is a separately passing prospective control in all six lanes: LiteEngine writes and checkpoints a memory stream, and FileReaderV8 reads the committed document without errors. Its issue-file blob is `b09cc10649079b70ee29dbbf24a72e290fcc9483`. Accumulating reads must still distinguish genuine end-of-file and incomplete final pages. Storage compatibility applies; any stream-wrapper expansion requires platform review.

#2819 controls cover length-255 binary/string keys and independent named indexes; regressions exercise longer keys, legal limits, merge containers and oversize rejection. BufferReader has other guards (89/170/183) which are not covered by this candidate. Do not broaden the exception to those guards. Retain runtime and storage compatibility checks.

#2367 has the same three failures on each OS but different TargetException first lines on net8 and net10. Like #2860/#2870, it stays with environment-aware contract design; no new normalization is justified. **Linq_Array_Navigation_Eval is an invalid proposed control**: it passes with all Eval assertions commented out. Linq_Document_Navigation_Eval has real row/nested navigation assertions and is a possible independent control after separate validation. Both methods are in frozen Mapper/LinqEval_Tests.cs blob `ed40e4af19ba2fd516cb3f46baa880e8bcbc2874`.

The four prepared contracts do not authorize source observations. #1829 shares the visitor file with unrelated source guards (49/50/51/52/53/137/138); any new unexpected guard pass must receive independent disposition. The other three bounded production paths have no directly matching source guard in the original audit inventory.

## Validation

Preparation checks verify all six raw report hashes, exact OR-filter selection and 24 real focused-baseline gates. Unit tests preserve the previous twenty-one contracts; reject wrong failure signatures, unexpected baseline passes, failed controls, missing/skipped/extra cases and out-of-scope paths; and verify lane requirements, compatibility, role delivery and contract/profile hash binding. No CI was launched for preparation.

Completed local suites: **103 gate tests and 198 controller/profile tests passed**. The existing unknown-issue profile test now uses unreviewed issue 9999 because #1444 is explicitly curated in this policy; its six-lane fallback assertion is unchanged.
