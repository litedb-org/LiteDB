# Manual sweep (2026-09-16)

The user's latest instructions supersede the hosted execution plan: manually fix
reproduced reports from PR #2885 in live priority-label order, obtain four
independent GPT-5.6 Sol reviews, resolve justified findings, then commit and push
each fix before selecting the next. P1 precedes P2 and P3. Starting with #1376,
every review wave must spawn four fresh gpt-5.6-sol agents with high reasoning
and fork_turns=none; no reviewer may carry context between waves.

GitHub controls were stopped before manual work: `BUGFIX_SWEEP_ENABLED=false`,
`bugfix-sweep.yml` is `disabled_manually`, and active fixer run 35084716213 is
confirmed `completed/cancelled`. Historical hosted journals and candidate branches
remain intact. Do not resume the hosted scheduler during the manual sweep.

Branch: `codex/manual-wholesale-bugfix`. It merges the automation documentation
head 7e09ef1d and accepted integration bbb0253b, preserving #2874, #2839, #2869,
and #1506. The unfinished #1002/#2811/#2590 candidate was salvaged, corrected
for custom setters and nullable ID types, and accepted below after four reviews.

`progress.json` retains the PR inventory, refreshed live labels, and explicit
current dispositions. It includes reports outside the former 24-task automation
queue; historical-only/unconfirmed cases remain distinct from current defects.
P1 order begins #2818, #2824, #2820, #2848, #2806; all have critical severity.
The complete scope remains unfinished.

## #2818 — WAL durability

Before: explicit and implicit commits only called ordinary stream Flush, allowing
acknowledgement before durable storage. Now confirmation-bearing WAL batches call
FlushToDisk before WAL-index confirmation; ordinary safepoints and empty commits
do not incur durable flushes. Encryption and caller-stream wrappers already
propagate this operation to FileStream.Flush(true).

A failed durable flush has an uncertain outcome because the confirmation may
already be persisted. The engine is closed with the original IOException (or a
wrapper retaining a non-I/O cause), preventing rollback and subsequent writes.

Validation: original regression baseline 2 failed / 1 passed; final durability
and WAL transaction boundary selection 25 passed. The broader 88-case selection
improved both #2818 cases and retained existing failures, with the known
intermittent #2814 WAL-growth case changing outcome. A focused unchanged-baseline repeat also reproduced #2814. Plain/encrypted vector
compatibility passed. Production builds passed both library targets with zero errors. Results/logs are retained under `/tmp/litedb-manual-results`
and `/tmp/litedb-2818-*.log` on this workstation.

Four independent `gpt-5.6-sol` reviews: `review_2818_1` through `review_2818_4`.
Initial review 3 found the explicit-commit flush-failure gap; it was fixed and all
four re-reviewed and approved the final implementation. Useful nits addressed:
WAL-index publication wording, update-only and post-safepoint flush counters,
empty-commit behavior. Additional filename/rollback-return-specific tests were
not added: they use the same already-covered FileStream/confirmed-header path,
and existing recovery tests cover their distinct functional behavior.

## #2824 — rebuild encryption and collation

Omitted API/SQL options preserve the current password and collation. Supplied
options select a new password (null removes encryption), while absent collation
preserves the current one. Effective settings are propagated to the reused
Direct/Shared settings only after installing the rebuilt file. Size metadata is
read before file moves so it cannot fail between installation and propagation.
Caller-owned options are not mutated.

Validation: the original ten cases failed before the fix. The SQL test had a
redundant FluentAssertions NotBeOfType assertion that itself fails on successful
null results. Removing it retains the stronger BeNull requirement and every
ledger/password/byte oracle. The corrected fixture still fails all ten cases on
unchanged baseline 25ef5063. The final selection passes 24 cases with one existing
skip, including eight new custom-collation Direct/Shared cases, ordinary rebuild
coverage and vector-format rebuild checks. Production builds pass both targets.

All four Sol reviewers re-reviewed and approved. Addressed findings: preserve
custom collation during password-only rebuild; remove fallible post-replacement
metadata reads; expand case-sensitive-key coverage and public XML documentation.
A proposed tri-state password-options redesign was refuted and the reviewer
agreed: supplied null Password explicitly removes encryption under #2824's
retained contract. This does not assert general crash-atomicity of file swaps.

## #2820 — validate files before repair

Length queries no longer mutate files or caller streams. Existing database
headers are fully validated before writable tail alignment; read-only alignment
only limits logical reads. Factory AES opens reject partial/zero-check encrypted
metadata instead of using the internal stream's legacy repair behavior. Header
identity/version validation precedes decoding arbitrary creation-time bytes.

Validation: corrected baseline 9 failures / 2 passing controls; final candidate
215 passed, including plain/encrypted torn tails, caller-stream byte preservation
and ownership, explicit v4/read-only upgrades, stream lifecycle and vector-format
coverage. Production builds and plain/encrypted vector compatibility pass.

Two expected v4 GUID literals in the frozen fixture test were demonstrably
incorrect (one even had invalid length). A standalone NuGet LiteDB 4.1.4 program
read the unchanged SHA-pinned encrypted fixture and returned
`4ac8f759-248f-4114-8be6-e510ad4e140d` (Jesse) and
`db503008-84d5-42d8-b372-d7616ea133f1` (Bob). Those exact literals were corrected;
all hash, byte, rejection, explicit-upgrade, sentinel and reopen checks remain.
Oracle program/output: `/tmp/litedb-2820-v4-oracle/`. Corrected tests still produce
the same 9 baseline failures. Four Sol reviewers approved the production changes
and independently checked the oracle correction and added coverage. No blocking
findings remained.

## #2848 — terminal transaction completion failures

Commit/rollback completion and monitor-release failures stop the engine, drain
transactions/resources, and preserve the first fatal exception atomically.
Later operations reject immediately, including while cleanup is still running.
Automatic error handling rolls back only active transactions. This also fixes
the reproduced #2169/#2803 post-commit checkpoint-error masking; it does not
claim confirmation of #2803's original SynchronizationLockException report.

Baseline: all three original #2848 cases fail. Final selection: 47 passed across
original issue cases, non-I/O failures, first-cause concurrency, frame cleanup,
WAL recovery/durability and checkpoint errors. Production builds pass both targets.
The older WAL boundary test's rollback-after-failed-Commit expectation was
updated to fatal rejection, retaining and strengthening all frame, WAL length,
monitor, independent recovery and checkpoint oracles. The issue regression now
requires exact original exception identity on subsequent writes. Four Sol
reviewers approved after addressing first-cause publication races and these
lifecycle expectations. New tests additionally cover non-I/O completion failures.

## #2806 — atomic uploads and explicit corruption errors

High-level Upload performs deletion, chunk writes and metadata replacement in one
transaction. It commits only a transaction it owns; failure aborts buffered
output and rolls back the active transaction, preserving the source exception.
Replacement repairs missing/orphaned chunks rather than asserting stale metadata
counts. Downloads reject missing/empty chunks and inconsistent length/count
metadata instead of succeeding with truncated content. Streaming OpenWrite keeps
its existing incremental semantics; callers can bracket it in a transaction.

Shared-mode support required fixing recursive mutex accounting: nested BeginTrans
preserves the caller transaction, every operation/reader balances its acquired
recursion, borrowed-engine exception cleanup preserves ownership, and disposal
releases in finally. This also passes the reproduced #2787 owner-alive mutex case.
Overlapping shared readers disposed out of creation order remain a pre-existing
lifetime limitation, explicitly deferred after reviewer agreement; arbitrary
cross-thread reader disposal is not claimed fixed.

Baseline: both #2806 cases fail. Final selection: 20 pass, including encrypted
interruption, owned/caller Direct/Shared transactions, deterministic dedicated
second-thread mutex checks, first/middle/last missing chunks, replacement repair,
empty/exact-multiple files, seek/EOF boundaries and corrupt metadata. Both
production targets build. Four Sol reviewers approved the final candidate after
fixing the mutex-recursion and borrowed-engine cleanup findings. The first async
worker check could reuse the owner thread; dedicated Thread coverage replaces it.

A broader net10 behavioral comparison (excluding source-context audit guards)
ran 1,339 baseline and 1,380 earlier P1-candidate cases: 32 existing failures became
passes, and no previously passing test became failing. All remaining failures
are still visible. This was before the final Shared mutex correction/additional
storage cases and is not a final full platform matrix.


## #1002 / #2811 / #2590 — atomic generated-ID assignment

Single and enumerable writes resume ID copy-back inside the engine transaction,
so setter exceptions roll back automatic and caller-owned transactions. Read-only
empty IDs and incompatible default String/ObjectId mappings fail before writing.
Captured reflected-setter identity preserves custom ResolveMember conversions.
Nullable int, long and Guid null/default values consistently request generation.

The hosted candidate was reused and its custom-setter regression corrected.
Baseline selection: 13 failed / 6 passed. Final selection: 40 passed, covering all
five insert/upsert/bulk surfaces, lazy input, caller transaction rollback,
custom/throwing setters, nullable copy-back uniqueness and reopen. Both production
targets build. Four Sol reviewers approved after addressing the nullable long/Guid
sibling gap identified in review; no unresolved substantive findings remain.


## #2871 — concurrent constructor-cache reads

The constructor cache uses ConcurrentDictionary for safe fast-path reads while
retaining the existing miss lock, serialized compilation and recursive interface
construction. Baseline: 1 failure / 1 control. Candidate: 54 constructor/mapper
tests pass, including 4,096 unique types racing four cache-hit readers. The ledger
and repeated issue cases pass; all library targets build. Four Sol reviewers
approved. Audit guard 132 now identifies the original unsafe Dictionary field
instead of a generic lookup snippet that also matched the corrected implementation;
the paired behavioral tests remain the proof of correctness.


## #2802 — virtual mapper dispatch for typed reads

Non-simple query results dispatch through ToObject<T>, whose base implementation
continues through virtual ToObject(Type, BsonDocument) and Deserialize. Either
public override is honored and its returned object is used. Scalar projections
retain their value-based conversion. The original ten failures now pass; the
complete mapper/query selection is 283 passed / one existing skip (baseline ten
failed / 273 passed / one skip). All library targets build. Four independent Sol
reviewers approved without findings.


## #2769 — preserve all enum integer backing types

Integer enum serialization follows scalar integer mapping: smaller types use
Int32; UInt32/Int64 use Int64; UInt64 keeps its exact bit pattern in signed BSON
Int64. Enum decoding now reads all 64 bits. String mode remains unchanged.
This preserves equality and round trips; unsigned range ordering remains the
existing signed BSON storage contract. Baseline: seven failures / eight controls.
Candidate: 81 mapper/enum/enum-array/dictionary tests pass, including independent
raw oracles, all eight backing types, high-bit UInt64, reopen and indexed queries.
All library targets build. Four Sol reviewers approved without findings.


## #2770 — row-dependent enum equality

String-mapped enum equality/inequality translates a row-dependent right operand
instead of evaluating it outside the query. Parameter detection excludes parameters
bound by nested lambdas while retaining captured outer dependencies, preserving
closed constant computations. Other numeric enum operators retain their previous
rejection instead of accidentally comparing/adding stored names. Both safeguards
address initial Sol findings and have regression controls. Baseline: one failed /
four passed. Final: 68 net8 mapper/enum tests pass; all library targets build.
Four final Sol reviews approved without remaining findings.


## #2779 — evaluate closed unsupported method calls

Existing resolver translations take precedence, preserving server runtime functions
such as GUID()/NOW(). Unsupported methods may be evaluated once and bound as a
parameter only if no free query-row dependency exists. String Format/Join and
unsupported Math.Round signatures now return no pattern and use that fallback;
row-dependent calls still reject before client execution. MidpointRounding is
never mistaken for an integer digit count. Explicit Guid.TryParse rejection stays.

Initial eager evaluation caused four compatibility failures and a Sol finding;
resolver-first dispatch fixed them. Review also exposed the Round fallback gap.
Original baseline: five failures / 29 controls. Final: 284 net8 mapper/query checks
pass, one existing skip; all library targets build. Added nested-capture rejection
and midpoint-mode controls complement the original exact plan/parameter/ID ledger.
All four final Sol reviews approved.


## #2860 — relative URI decoding and precise max-depth diagnostics

The built-in URI decoder accepts RelativeOrAbsolute text, retaining relative paths
including /a/b while preserving explicit HTTP/URN/file schemes. Serialization is
unchanged. The same report's diagnostic contract now uses Type.FullName to identify
nested types with identical simple names. Baseline: eight failures / six controls.
Final: 66 net8 URI/mapper tests pass, including independently authored BSON and
both URI-kind controls. All library targets build; four Sol reviewers approved.


## #2858 — culture-independent SQL grammar

Command dispatch/log labels and INSERT auto-ID type tokens use invariant casing.
Payloads, identifiers and database collation stay on their existing paths.
All four original Turkish-culture failures pass, with uppercase controls and
persistence/transaction/rebuild oracles retained. The broader SQL selection has
70 passes and nine unrelated failures; each failure matches the full behavioral
baseline (LIKE, grouping and SQL audit cases). All library targets build and all
four Sol reviewers approved without findings.


## #1159 — repeated fluent Ignore calls

The cached entity mapper remembers successfully ignored CLR member names across
fluent builder instances. Only repeated Ignore calls use that ledger; other
mapping operations and invalid/null selectors retain their existing validation.
Renamed BSON fields do not affect CLR-name tracking. Original baseline: one failure.
Final: 54 net8 mapper tests pass, including unrelated-field persistence and added
strict-validation controls; all library targets build. Four Sol reviewers approved.
The inherited string-based GetPath parser limitation was noted and left separate;
it affects prior member resolution and is not introduced by the ignored-name ledger.


## #2867 — inherited mapped-type ID convention

ID selection honors the reflected mapped type after explicit BsonId/plain Id and
before the legacy declaring-type fallback. LINQ member access uses its mapped
owner, and MemberInit uses the constructed result type, keeping query/projection
paths consistent with serialization. Transparent reference upcasts retain the
underlying mapped owner; other conversion categories keep their prior handling.

Baseline: three failures / one control. Original typed-query checks caught the
initial serializer/query mismatch, also identified by Sol review. Review then
caught explicit base casts; both cast/as and typed projections now have controls.
Legacy inherited BaseTypeId and explicit-attribute precedence remain covered.
Final: 305 net8 mapper/query/auto-ID/DbRef tests pass, one existing skip; all library
targets build. All four final Sol reviews approved.


## #2225 — inherited private setters

When reflected inherited property metadata omits its private setter, setter creation
re-fetches that exact property from its declaring type with DeclaredOnly. Closed
generic type arguments are retained; genuine or hidden getter-only declarations
remain unwritable. The normal DefaultSetter/custom-setter pipeline is preserved.
Original baseline: one failure. Final: 80 net8 mapper/auto-ID tests pass, including
identity-preserving reopen/update, generated inherited IDs and hidden getter-only
rejection. All library targets build; four Sol reviewers approved without findings.


## #2578 — stored-name constructor binding

Constructor parameters first match CLR member names, then stored BSON field names
including _id and BsonField aliases, using ordinal case-insensitive comparison and
exact declared types. Materialization invokes the selected ConstructorInfo directly,
so runtime argument subtypes cannot redirect an annotated object overload to an
unannotated string overload. Constructor precedence and post-construction member
population remain compatible. Baseline: one failure / eight controls. Final:
56 net8 mapper/constructor tests pass, including raw stored-name and overload-trap
controls; all library targets build. Four Sol reviewers approved without findings.


## #2873 — explicit factory-owned materialization

New CtorOnly(factory) returns the factory result once after actual _type validation
and mapper initialization, without other instantiators or member/dictionary
population. Existing Ctor(factory) retains population and resets the opt-in when
registered later. Null factories reject; a factory's null result is returned once
without retry, consistent with the documented direct-result contract.

Original baseline: two failures / one compatibility control, including the reported
QuantityRange/Enum model. Final: 59 net8 mapper/constructor tests pass, covering
factory-owned invariants, exact identity, setter/instantiator suppression, legacy
registration restoration, null contracts and the real reported persistence case.
All library targets build; four Sol reviewers approved without findings.


## #2797 — terminating LIKE wildcards and complete matches

The matcher uses last-percent backtracking with an advancing retry position,
requires full input/pattern consumption, and treats underscore as one UTF-16 code
unit. The index prefix helper retains residual checks for a terminal underscore;
only an exact literal or lone trailing percent can skip them. Per-character
collation semantics are retained; IndexLike linguistic range behavior is #2144.

Baseline bounded runner reproduced wrong results/nontermination; four existing
wildcard audit cases failed. Final: 229 net8 query/wildcard tests pass, one existing
skip, including 21,483 independent anchored-regex comparisons and indexed/unindexed
boundary cases. ReproRunner report confirms package5.0.21 still reproduces while
latest returns exact exit10 plus VERIFIED_2797 with Met=true. Latest manifest is
green/noRepro with those strict expectations. All library targets build; four Sol
reviewers approved. Report: /tmp/litedb-manual-results/2797-repro-final.json.


## #2144 — index LIKE respects collation

Only ordinal/ordinal-ignore-case collations use prefix range seeks; linguistic
sorts may interleave nonmatching prefixes and now use a filtered full index scan.
Planner cost and plan text describe the chosen path. Ordinal seeks use matching
comparison rules, filter supplementary case pairs through scalar LIKE, and never
stringify non-string keys into matches. This trades linguistic-prefix speed for
correct results; ordinal seeks remain available and other selective predicates
can compete against the honest scan cost.

Baseline: both en-US/ja-JP prefix cases fail after indexing. Final: 236 net8 query
tests pass, one existing skip. Added hand-written case/type ledgers run before and
after indexing/reopen in both directions; a Deseret supplementary-case regression
checks the ordinal-ignore-case residual. All library targets build. Four Sol
reviewers approved; the trailing-whitespace nit was corrected.


## Stage regression check and #2802 follow-up

The net10 behavioral suite reaches 1,200 passes, 222 failures and eight skips,
fixing 99 baseline failures. One previously passing document-upgrade test exposed
a raw BSON callback regression: ToObject returns BsonDocument unchanged. Raw
document queries now retain their previous virtual Deserialize path while POCO
queries keep virtual ToObject<T>. A replacement-identity/count regression proves
that callbacks execute once and do not alter stored data.

Follow-up validation: 314 net8 passes, one existing skip and seven audit failures
confirmed against the full baseline. The document-upgrade test now passes. All
library targets build; four additional Sol reviews approved without findings.


## #2800 — execution-local nested expression parameters

MAP, FILTER, SORT selectors, parameterized array indexing and array predicates
forward the current execution's parameter document into nested delegates. Cached
inner expression instances remain untouched, avoiding stale first-query values
and races between queries sharing compiled expressions. Public execution still
uses each expression instance's Parameters.

Baseline: three reproduced query forms fail. Final: 288 net8 expression/query/LINQ
tests pass, one existing skip; added changing sort/index values, doubly nested
interleaved enumerators and parallel filter executions. All library targets build.
Four Sol reviewers approved without findings.


## #2801 — reject unusable object-constructor collection values

BsonValue(object) now throws ArgumentException for collection inputs and directs
callers to BsonArray, BsonDocument or BsonMapper. This is the issue's explicitly
accepted rejection contract: construction can no longer succeed with Array or
Document type while the corresponding adapter is null. Existing BSON collections
passed through object also reject; collection inputs are never enumerated. Valid
boxed scalar, string, binary, vector, date and scalar-BsonValue inputs retain their
prior behavior. Canonical collection constructors and mapper paths remain intact.

Baseline: eleven collection failures. Final: 349 net8 BSON/mapper/expression/query
tests pass, one existing skip; explicit lazy-rejection and supported-type controls
pass. All library targets build; four Sol reviewers approved without findings.


## #2805 — culture-independent supported Unicode index names

Both scalar and vector auto-name paths retain letters/digits using the same
UTF-16 character categories accepted by the existing index-name validator. This
preserves supported BMP Unicode names and removes Turkish-I regex dependence.
Ordinary ASCII names and punctuation filtering remain unchanged. Supplementary
characters remain unsupported by the existing index-name validator; simply
copying surrogate pairs would still fail validation, so that review suggestion
was refuted as a separate identifier/naming expansion.

Persisted names are not silently renamed or reused by expression: different
names for the same expression are valid and can have different uniqueness/vector
options. An old Turkish-generated Name index for $.IName remains present when
the canonical IName index is first created. Further auto-name calls are idempotent
across cultures. Callers may explicitly DropIndex("Name") to remove the obsolete
legacy index after creating the canonical replacement. This one-time duplicate
is intentional; expression-only deduplication was refuted as incompatible.

Baseline: five failures. Final: 239 net8 scalar/vector query tests pass, one skip;
all six final issue cases pass, including persisted legacy scalar/vector names,
reopen, stable catalog entries, query results and explicit old-name removal. All
library targets build. Four Sol reviewers approved the resolved scope/contracts.


## #2843 — terminate shell continuation at EOF

Initial and continuation EOF use the normal exit command so the database is
disposed. Incomplete SQL is discarded before execution/history. Exhausted
AutoExit queues return EOF instead of endlessly appending synthetic exit text.
Complete queued and multiline commands retain normal execution.

The real-shell regression reproduced the bounded infinite loop before the fix.
Final Release shell build and runner pass (VERIFIED_2843), covering piped and
queued EOF, nonexecution of unfinished INSERT, complete/multiline positive
controls and committed data read through separate processes. The runner reads
the actual framework from the project instead of assuming net8. Four Sol
reviewers approved; their generated Python cache cleanup nit was addressed.


## #2845 — exact finite JSON double round trips

Finite doubles use invariant G17 formatting; integer-looking tokens gain .0 to
preserve BSON Double classification. Writer and reader explicitly preserve the
negative-zero bit, including older parsers that normalize it. NaN/infinities
retain their existing JSON null representation. The reader portability finding
was addressed before final approval.

Baseline: seven failures, one control. Final: 71 net8 JSON/BSON/expression tests
pass, including 2,000 seeded finite bit samples under de-DE, repeated exports,
independent invariant parsing, ±0, extrema, normal/subnormal boundaries, large
integers and nonfinite controls. All library targets compile; net462/net481 tests
were not executed because no legacy .NET Framework runtime is available here.
Four Sol reviewers approved the final writer/reader revision.


## #2764 — encrypted seeks use logical positions

Seek resolves Begin/Current/End against the visible stream, applies the hidden
header offset exactly once, and returns the visible position. Checked arithmetic
and before-start rejection prevent invalid requests from moving into the hidden
encryption header. The backing seek always uses Begin, avoiding wrapper-specific
relative-seek behavior.

Baseline: five failures. Final: 40 net8 encryption/durability tests pass, including
readback of distinct decrypted pages, physical/logical position ledgers and
negative/invalid/overflow failure preservation. Plain/encrypted vector file
compatibility and all library target builds pass. Four Sol reviewers approved;
additional per-origin overflow tests were suggested as optional and judged
redundant with the existing checked-arithmetic and overflow control.


## #2767 — fill fixed-size reads across partial stream results

Current data/WAL pages, cache reads, V7/V8 recovery readers and encryption
metadata accumulate short reads. Required pages/metadata reject EOF; optional
rebuild prefix probes may stop at EOF. Caller-stream factories honor raw access
for version detection and legacy encryption. AES blank-page normalization probes
only available bytes and modifies only the returned range.

Reviews found remaining rebuild-prefix and V7 read sites; both were corrected.
Added tests cover one-byte, 257-byte and 4095-byte current reads, encrypted/plain
WAL replay, empty/nonempty V8 recovery, immutable plain/encrypted V7 fixtures,
content/byte ledgers and an oversized encrypted-read buffer sentinel. Valid
short reads from the 16 KiB format probe remain supported for an 8 KiB database.

Final: 59 focused net8 passes, one existing skip. A broader rebuild selection
also exposed 13 unchanged baseline failures in other pending reports. The full
net10 stage before final recovery additions had 1,268 passes, 185 failures and
eight skips, fixing 135 baseline failures with no newly failing former passes.
Plain/encrypted vector compatibility and all library targets pass. Four final
Sol approvals; the unrelated BOM and buffer-range nits were fixed. Zero-count
helper argument validation was deferred because these internal callers supply
valid positive ranges.


## #2827 — count unflushed transaction allocations in traversal limits

Each scalar index/data traversal adds a widened allowance from the transaction's
actual NewPages ledger to its physical data/WAL bound and captures that budget
once. The ledger includes new and recycled pages and survives safepoints. This
covers valid unflushed chains while retaining finite corruption detection.

Review caught an early duplicate property name and a more serious proposal to
trust header LastPageID, which corrupt files can inflate. The final implementation
uses only the trusted allocation ledger; a forged uint.MaxValue header self-cycle
regression proves the guard still trips within the bounded test enumeration.

Final: 361 net8 engine/query tests pass, four existing skips, including 10,000-row
indexed rebuild, 6,000-row insertion/index creation in one transaction and real
cycle detection. Plain/encrypted vector compatibility and all library targets
pass. Four Sol reviewers approved the final ledger-based revision.


## #2819 — read extended sort keys and reject oversized keys before writing

The streaming sort-key decoder reads the full ten-bit string/binary length used
by the existing writer. Other types retain their exact raw type byte, including
Vector=100; the first draft's vector masking regression was caught by three
existing query tests and review, then corrected. SortContainer validates the
serialized size before writing, preserving the expected code-111 error.

Final: 278 net8 query/BSON tests pass, one existing skip. The 16 issue cases cover
255/256-byte boundaries, maximum 1023-byte serialized keys, oversized rejection,
ascending/descending results, indexed controls and full payloads across multiple
sort containers and page slices. All library targets build; four final Sol
approvals. Existing less-than wording at the inclusive size limit is retained
for compatibility with the documented error contract.


## #2841 — validate terminal state before metadata prechecks

Drop/Rename validate after public argument checks and before inspecting the
transaction locker. Pragma reads validate before header access, so both changed
and same-value setter calls preserve a terminal error instead of reporting an
open transaction or returning success. Healthy transaction/no-op semantics remain
unchanged.

Final: 123 net8 engine tests pass, three existing skips; all three final issue
tests pass. Coverage includes the exact stored exception instance (review nit
addressed), getter/no-op/changed setter, collection operations, healthy explicit
transactions and normal disposal. All library targets build; four Sol approvals.
GetCollectionNames validation was noted as a separate existing behavior.


## #2322 — translate generic enum Equals with CLR type identity

Instance Enum/Object/ValueType Equals calls on statically known enum receivers
rewrite to the existing equality mapper only for the same enum type. Closed
boxed arguments are evaluated once and checked by runtime type; null, primitive,
string and foreign enum values compare false. Same-enum row fields compare in
the database; foreign enum fields compare false. Unknown object-typed row values
remain unsupported. Method-free object/Enum/ValueType boxing is unwrapped without
erasing user conversions.

Final: 292 net8 mapper/query passes, one existing skip; all four issue cases pass
in both enum storage modes. Controls include undefined values, changed captures,
all three boxing forms, reverse receivers, nullable captures, foreign enum
identity and field comparisons. ValueType boxing and optional coverage findings
were addressed. All library targets build; four final Sol approvals.


## #1376 / #2810 — dictionary presence and general Any/All predicates

String-key dictionary ContainsKey calls use a BSON field-presence expression,
including Dictionary, IDictionary, IReadOnlyDictionary and BsonDocument. Present
null fields match; absent fields do not. Keys remain parameters, including empty,
punctuated and quoted names. Lookup follows stored BSON OrdinalIgnoreCase field
semantics independently of value collation; CLR dictionary custom comparers are
not persisted. Non-string dictionary key types remain unsupported. Generic-only
dictionaries stored as key/value arrays, null keys and non-string runtime keys
fail explicitly. Interface method maps exclude hidden ContainsKey methods with
unrelated behavior. Reviews identified each of these boundary cases.

Any/All retain existing simple translations and add a MAP-based per-item fallback
for compound predicates, captured Contains, dictionary ContainsKey and nested
quantifiers. This also fixes #2810 for embedded/referenced lists and captured
List/IEnumerable inputs. Empty-sequence semantics follow ANY/ALL. Nested captures
of an outer collection item fail explicitly because the BSON evaluator has one
current-item slot; root-document references remain supported. The original
predicate renderer moved to a partial file to respect the source-size cap.

Review also caught lost $id mapping in compound referenced-list predicates.
Lambda parameter scopes now retain reference metadata across repeated IDs and
unrelated root members; sequence metadata survives Where and identity Select.
Each pattern-rendered lambda derives metadata from its own source, including
Where/Select composition and identity projections. Embedded/reference controls
cover repeated IDs, root lookups, filtering and composed projections.

Validation: 325 net8 mapper/query/expression tests pass, one existing skip.
All library targets build. Final net10 stage: 1,343 passes, 306 failures and eight
skips, with no formerly passing baseline test regressing. There are 159 passing
baseline failures in this run; one is #2324's intermittent mapper-configuration
race, which failed in the baseline, five earlier stages and the preceding run.
That race remains unresolved. Four fresh Sol high-reasoning reviewers approved
the final revision with no findings above nits.

Every review wave uses four fresh gpt-5.6-sol agents at high reasoning with
fork_turns=none, without reading prior reviews. Agents are never reused.


## #1545 — bind captured grouping keys as values

Grouping members whose expression does not depend on the query row are evaluated
and bound as captured values. Query-dependent grouping keys retain @key. Tests
cover original forward/reverse groups, changed captures in one reused predicate,
composite key member access and the server key binding.

Validation: 312 focused net8 tests pass, one existing skip; all library targets
build. Four fresh Sol high-reasoning reviewers approved with no findings above
nits.


## #2367 / #1908 — bind captured indexed values and their members

Closed collection indexes, LINQ element selectors, member chains and unary
operations bind their CLR values once per translation. Captures are read again
on each translation. This avoids applying element getters to collections and
unsupported parameter[index] syntax. Supported row indexes retain server paths.

Coverage includes mutable arrays/lists/dictionaries, custom and multidimensional
indexers, explicit IndexExpression, quoted keys, First/Last/Single/ElementAt and
default variants, Min/Max, predicate/Where/ToArray composition, conditional and
coalesced receivers, custom/numeric conversions, negation and short-circuiting.

Volatile clocks and GUID generation retain supported server translations;
deterministic constants such as Guid.Empty remain captures. Unsupported runtime
wrappers, positional indexes and mixed captured branches fail explicitly before
reading unchosen getters. Dynamic query-row positional indexes are rejected
instead of compiling an unbound parameter or being parsed as array filters.
Supported runtime predicates using boolean operators remain supported. Runtime
unary operators are limited to correctly supported boolean/identity/widening
forms. A root-level branch check prevents eager reads bypassing the local guards.
Row dictionary keys now use JSON escaping, with quote/backslash/newline controls.
Empty row keys fail explicitly: MEMBER_PATH treats an empty name as the parent,
so they cannot safely use the current path grammar. Captured empty keys still
use CLR dictionary semantics. Review attributed the empty-key error to caching,
but inspection confirmed the direct parent-return behavior in MEMBER_PATH.

A claimed regression for Select(...Now...).First().Year was checked on the
previous commit: it already rejects MAP(...)[0] in the BSON parser. The supported
predicate-First variant keeps NOW(); unsupported indexing remains rejected
instead of being silently frozen.

Validation: 354 focused net8 passes, one existing skip;
four fresh independent Sol high reviewers report no findings above nits.
Full net10 validation has 1,392 passes,
300 failures and eight skips, with no original baseline pass or preceding-stage
pass regressing and no new failing test. There are 164 passing original baseline
failures. The escaped-key change also passes the existing H50 injection audit.
All library targets build.

Full testing also exposed an intermittent failure in the added #2767 blank-page
test: AesStream computes its zero-block sentinel from an uncleared rented buffer.
An isolated probe that dirties the pool reproduces this deterministically. A
separate storage follow-up is required after this fix.

Closed scalar conditional/coalesce/boolean branches containing element accesses
are evaluated atomically, including reused conditions and skipped empty arrays.
Two review reports about ordinary, non-indexed captured-member negation and
ordinary runtime/getter branches are existing translator limitations outside the
#2367/#1908 element-access reproductions. They do not enter the added element
handlers and follow unchanged legacy paths; they are not claimed as repaired.

Runtime branch validation is scoped to the affected branch or runtime sequence;
unrelated closed branches can bind independently while NOW() stays server-side.
Resolver-miss runtime rejection is limited to element-bearing calls, preserving
ordinary CLR method-capture behavior. Compatibility controls cover both cases.

Closed array-length expressions also bind in CLR, so a captured null element
throws rather than silently becoming LENGTH(null) = 0. Runtime-produced arrays
retain the supported server LENGTH translation.

Runtime unary validation preserves methodless unary plus, identity reference
casts and standard numeric widening (including checked widening), while still
rejecting narrowing and custom operators that the server cannot represent.

The final review wave withdrew a claimed First-pipeline regression after checking
that indexedSource is populated only for IsIndexAccess calls. The cited positive
tests also pass in both focused and full runs.

## #2767 follow-up — initialize the encrypted blank-page sentinel

AesStream rented a scratch block without clearing it before decrypting it to
identify unwritten ciphertext pages. Pool reuse could therefore turn blank pages
into garbage. Clear the 16-byte block and bound the temporary stream to it.
The existing partial-read test now dirties the shared pool first: it fails before
the change and passes after, including its untouched destination-tail check.
The pool reuse control relies on the current same-thread Shared implementation;
the isolated probe independently confirms the observed failure and repair.

Validation: 49 focused storage tests pass; isolated probe reports blank=True and
tailPreserved=True; ordinary/vector compatibility (plain and encrypted) passes;
all production targets build. Four fresh independent Sol high reviews report no
findings above nits.

## #2739 — preserve DbRef mapping in captured update assignments

Captured UpdateMany member assignments now serialize through the destination
member, preserving $id/$ref metadata, polymorphic $type, mapped fields, and list
null omission. Conditional/coalesce branches retain that destination context.
BsonRefId expression markers retain their server translation; mixed initializer
lists support captured references and omit captured nulls with correct commas.
Ordinary row reference paths remain unchanged. Capture values are read anew for
each translation. Coverage includes on-disk reopen and Include after the target
record changes, distinct destination collections, null/empty lists, polymorphism,
conditional/coalesce branches and mixed marker lists.

Validation: 296 focused net8 passes, one existing skip; full net10 1,401 passes,
298 remaining failures and eight skips. No original-baseline or preceding-stage
pass regressed; 165 original baseline failures now pass. All production targets
build. Four fresh independent Sol high reviewers report no findings.

## #1224 — lossless unsigned integer BSON conversions

Implicit UInt64 values use signed BSON Int64 bits, matching BsonMapper, instead
of Double. Reverse conversion accepts Int32/Int64 via signed widening and
unchecked reinterpretation; other BSON types retain strict conversion errors.
The legacy implicit-conversion test now expects that signed representation.
Independent wire bytes cover zero, small integers, both signed halves, patterned
bits and UInt64.MaxValue. Existing reopen/secondary-index/FindById regressions
pass; new Int32 and noninteger controls cover reverse conversions.

Validation: 103 focused net8 tests pass, all production targets build, and four
fresh independent Sol high reviewers report no findings above nits.

## #1715 — isolate parameters when composing queries

Query.And/Or copy each operand's bindings to distinct parameter names and rewrite
only parameter tokens. Quoted strings/field names and standalone current-item @
paths retain their meaning. Unbound names are renamed too, preventing accidental
binding by the other operand. The new parameter document snapshots bindings
without changing either input. Nested and variadic composition use the same path.
Renamed group-key aliases remain available to GROUP BY, including composition
after a prior execution has populated runtime key values.

Coverage includes both operand orders, truth tables, repeated/case-insensitive
names, positional parameters, nested filters, hostile quoted text, unbound names,
input mutation controls, and repeated composed Having execution.
Validation: 314 focused net8 passes/one existing skip; full net10 1,430 passes,
284 remaining failures/eight skips, no pass regressions against the original
baseline or preceding stage. There are 178 passing original baseline failures.
All production targets build. Four fresh independent Sol high reviewers report
no findings above nits.

## #1087 — retry native Linux lock contention

The lock classifier recognizes raw Linux errno 11, preserving existing codes
32/33 and rejecting wrapped Win32 error 11 or unrelated I/O failures. The real
two-process baseline failed after one attempt. Fixed source retries 21 times over
504 ms and acquires the lock after controlled release; package 4.1.4 retains its
expected failure. The latest manifest requires noRepro/exit 10/NO_BUG_1087 and the
README separates historical reproduction from current verification.

Validation: 23 focused net8 tests pass (both retry helpers, timeout, non-lock
propagation and platform/error-code controls); two-process package/latest run
matches both expectations; all production targets build. Four fresh independent
Sol high reviewers report no findings above nits; the documentation nit is fixed.


## #2163 / #1848 / #2815 / #1958 — own physical files and preserve rebuild isolation

Filename engines acquire a native lock before accessing the WAL: exclusive for
writers and shared for read-only engines. Locks follow the physical file rather
than its textual path; an identity recheck rejects an opener delayed across
atomic replacement. Windows uses LockFileEx, Linux/Darwin use OFD locks, and
older Linux kernels/FreeBSD use flock with compatible stream opening. Unsupported
native/virtual filename APIs fail closed; caller streams remain available.
Read-only close neither checkpoints nor opens/deletes a WAL writer.

Rebuild rejects active transactions before changing state, checkpoints the
original even with CHECKPOINT=0, and holds both original/replacement ownership
through File.Replace. Its backup is now a complete standalone data file, so the
live-WAL backup test reflects that contract. #2450's separate repeated-backup
retention failure remains pending. Checkpoint or cleanup failures abort rebuild
and preserve a terminal causal error. Lifecycle serialization, generation-bound
service capture and stale-state checks keep delayed operations from closing a
replacement engine. Registration happens outside the lifecycle lock to avoid
blocking transaction completion behind a waiting checkpoint.

Settings freeze storage/access fields and absolute paths while preserving the
established live ReadTransform callback. Ambiguous DataStream+Filename settings
are rejected before file access. Independent sort engines use private scratch
streams, immediately unlinked on Unix and delete-on-close on Windows. Disposal
cannot race first creation or delete an unrelated recreated pathname.

Review resolutions include native ABI/old-kernel handling, readonly recovery's
return to shared ownership, atomic close/rebuild ordering, mixed settings,
partial checkpoint failure, path retargeting, alias exclusion, and scratch-file
crash cleanup. Automatic reopening after uncertain I/O was refuted as unsafe;
callers must dispose/reopen. Stable pathname use across later opens is explicit:
physical locking does not relocate a filename-derived WAL between aliases.
Unsupported modern OFD filesystems fail closed instead of switching lock
protocols while another owner may already use OFD locks.

Validation: 212 focused net8 cases pass. The latest full net10 run has 1,476
passes, 275 remaining failures, eight skips, and no pass regressions against the
original baseline or preceding committed stage. After the backup-contract test
update, all 24 ownership cases pass; both #2450 tests still fail on the existing
extra numbered backup. All production targets build and net462 tests compile.
The two-process #2163 runner is green: package 5.0.21 loses acknowledged row 202;
latest rejects the conflicting owner and preserves every receipt/index after
reopen. Ordinary/vector compatibility passes for plain and encrypted files.
Linux ownership/rebuild/readers pass, including the flock path under an injected
older reported kernel version (not actual old hardware). SIGKILL after plain
and encrypted spilling sorts leaves no scratch files; late scratch creation
after disposal is rejected. Windows/Darwin/FreeBSD backends received static ABI
review, not runtime execution here. Four fresh independent GPT-5.6 Sol high
reviewers in the final wave report no substantive findings.

## #2545 / #2785 — database-bound WAL generations

New transactional WALs have an unconfirmed identity prefix; reserved data-header
bytes contain a random database identity, current checkpoint generation, previous
generation, completed-WAL fingerprint, and checksum. A restored data file rejects a later generation;
an exact completed predecessor cannot roll back later checkpointed commits. Foreign
and orphan WALs are rejected before replay, initialization, alignment repair,
or recovery import, preserving both files. Read-only opens ignore completed
WALs without truncation. Ordinary v8 and vector v9 remain unchanged.

Durable order is identity-before-first-frame, then on checkpoint: data flush,
full WAL durable flush, new generation/fingerprint flush, WAL truncation and
flush. Legacy migration truncates its
unmarked WAL before installing identity. Every uncertainty while publishing
metadata or truncating WAL stops the engine, including explicit Commit's automatic
checkpoint and non-IOException caller-stream errors. Pre-publication noncritical
access failures preserve established retry behavior. ConcurrentStream truncation
now clamps its logical position, preventing encrypted disposal from restoring a
truncated WAL through a zero-byte write. All transaction scans ignore the prefix,
including a wrapped transaction ID of zero.

Legacy logs can only prove creation-time identity when a confirmed header is
present. Older writers must checkpoint before handing protected files back;
their prefixless WALs are rejected, and generation guarantees cannot span their
checkpoints. Damaged/partial prefixes intentionally fail closed. A review request
to silently discard a torn initial prefix was not adopted: the same bytes can
also represent damage to a formerly committed WAL. New crash tests establish
that no transaction is published on initial identity flush failure and that a
torn prefix preserves all checkpointed receipts and both original byte arrays.
The manual recovery limitation is explicit in docs/wal-recovery-identity.md.
Recognizable nonzero modern identity metadata also rejects a structurally damaged
prefix beside legacy data, preventing accidental fallback to unmarked replay.
Eight structural-field/magic corruption cases exercise a header-free update WAL.
Recognition also requires the normal header signature: a real legacy update
whose user payload spells the identity magic at byte 109 must still replay.

Validation: 78 focused net8 passes,
including plain/encrypted orphan preservation, two-generations-old rejection,
initial-binding interruptions, and vector identity/format preservation. The
latest full net10 run has 1,549 passes, 271 remaining failures and eight skips,
with no pass regressions against the preceding committed stage. All production targets build and net462 tests compile.
An actual NuGet 5.0.21 probe passed plain/encrypted legacy WAL replay, new-prefix
reading by the old engine, an old-engine checkpoint, and reopening its results.

The predecessor fingerprint is a 128-bit truncation of SHA-256 over the complete
logical WAL. A later old-engine append, modification, or restored subset cannot
be silently discarded as completed. A real 5.0.21 append after a simulated
post-checkpoint crash is rejected with both files byte-for-byte preserved, for
plain and encrypted files. A FileStream fault fixture distinguishes Flush()
from Flush(true): rolled-back safepoints are durably flushed before their bytes
enter the published fingerprint. Fingerprinting reads whole pages for the
existing encrypted-stream alignment contract. These latest changes pass 78
focused net8 tests. All four fresh independent Sol high reviewers in the sixth
wave report no substantive findings. The full-suite rerun archived a leftover
`demo.db` from this patch's earlier unpublished metadata layout; no released
layout was changed or migrated.

### #2808 v4 security release candidate (deferred by user)

The existing v4 branch contained only the earlier Process/assignability patch.
Prepared and pushed `a13746f5` on `codex/manual-v4-security`, based on v4
`3af2be2b`: v5's 35 exact denied names, an application binder hook, retained
assignability and Process error 215, and an explicit allow-list example.
The default policy deliberately matches v5; it does not recursively inspect
permitted generic/member graphs or turn assembly-looking entries into namespace
bans. These inherited limitations and the trusted custom-binder boundary are
explicit in the candidate's `docs/v4-security-release.md`.

Three review waves used four new independent GPT-5.6 Sol high/no-fork agents each.
The final wave found no actionable defects. Valid test-path/native-runtime gaps
from earlier waves led to a dedicated v4 workflow and CLR2/CLR4 harnesses. Exact
policy limitations were documented rather than claiming a general deserialization
sandbox. Local validation: 55 security tests, all four signed extracted framework
assets run under .NET 8, net35/net40 harness compilation, assembly version/key
checks, and actionlint. Windows CLR2/CLR4 runtime validation also passed in CI run `35143229609`.
The workflow publishes only the same artifact that passes all gates, requires an
annotated v4.1.5 tag already integrated into v4, and defaults publication to off.
The user explicitly deferred v4 work and requested that the reviewed candidate
remain unpublished. The ledger records #2808 as `deferred-user`. No v4 integration,
stable tag, release, or advisory mutation has been made.

### #1192 — dictionary schemas and runtime metadata serialization

Dictionary mapping recognizes standard collection types implementing the legacy
IDictionary indexer instead of indexing every concrete generic type's argument array. Inherited
Dictionary schemas survive unrelated tags and reordered subclass parameters;
opaque legacy adapters retain established declared mappings, including explicit
read-only interfaces and multiple side views. Opaque two-argument legacy adapters
retain their historical convention. The reflection cache uses ConditionalWeakTable, with
collectible runtime, contract-argument, and declared-interface coverage.

Delegate and MemberInfo values become BSON null after exact declared/runtime
custom serializers and the virtual SerializeObject hook can handle them. This retains normal
exception diagnostics without invoking delegates or traversing reflection graphs.
The existing public Serialize/Deserialize APIs already provide BsonValue mapping.

Review-driven regressions cover generic legacy adapters, custom factories,
discriminators, direct generic implementations, extra/reordered type arguments,
opaque side stores, mutable/read-only views, and collectible assemblies. Claims
that IReadOnlyDictionary is covariant were refuted using CLR generic-parameter
attributes and assignability: both parameters are invariant. Framework API
availability claims were checked with successful net462/net481 compilation.
Custom dictionary interfaces cannot establish backing-store identity through
reflection; opaque adapters preserve the selected declared schema rather than
claiming to validate an arbitrary custom implementation's semantics.

Serialization preserves historical object/object BSON shapes for non-generic
concrete types and interfaces, including erased non-generic BCL subclasses.
Deserialization separately resolves dictionary interfaces, discriminators, and
custom factory results without indexing missing generic arguments.

The final focused selection passes 50 cases on net8, including mapper inheritance.
All production targets build, and net462/net481 tests compile. Full net10 validation
(`p2-1192-w18.trx`) records 1601 passed, 266 failed, and 8 skipped, with no previously
passing regressions against 67b214cc. Both original reproductions and audit guards
133/134 now pass; an unrelated intermittent pass is not counted as a fix.
Eighteen review waves used four fresh independent Sol high agents apiece. In the
final wave, three reviewers found no issues. The fourth proposed changing consumed
`_type` dictionary-entry handling; exact baseline source already retains that key
and rejects non-string-convertible keys, so this separate pre-existing limitation
was refuted rather than changing persisted dictionary semantics here.

### #1829 — PredicateBuilder invocation binding

Invoked lambda parameters are substituted before LINQ query scope analysis,
so swapping composed predicates no longer changes root `$` references into item
`@` references. Nested lambda binders are renamed to prevent capture and preserve
shadowing. The old mapper fixture passed an unbound foreign parameter; it now
constructs a valid composable expression and compares compiled behavior.

Closed client-evaluated subtrees retain their atomic evaluation boundary.
Row-dependent invocations accept parameters, constants, and document member
paths. Captured fields/getters, calls, computed expressions, and indexed arguments
are explicitly rejected rather than being duplicated, omitted, or reordered.
General support for those previously unsupported invocation shapes remains outside
this correction. Regression tests cover volatile GUID generation, omitted fields,
closed getters, captured indexed evaluation, and nested binding collisions.

The focused selection passes 97 tests; the combined mapper/LINQ selection passes
147 on net8. All production targets build and net462 tests compile. The full
net10 run (`p2-1829-integrated-clean.trx`) has 1611 passed, 264 failed, and 8 skipped,
with no previously passing regressions. An earlier run hit an orphan WAL before
mapping because TempFile uses five-character random names in a shared directory;
all 20 AutoIdAssignment tests passed on recheck and a fresh temporary directory
removed that unrelated collision. Four review waves used four fresh independent
Sol high agents each; the final four reviews were clean.

### #1344 — overlapping storage writes and cursor snapshot ownership

The reproduced overlapping-upload bug is covered by the prior #2806 upload
transaction. The original fixture incorrectly required every intermediate chunk
to be full; it now checks actual chunk counts, contiguous IDs, nonempty bounded
chunks, exact content, downloads, orphan ownership, and reopen. This corrected
fixture still fails the original baseline with duplicate keys/wrong payloads.

Delete now removes metadata and chunks in one write transaction, and SetMetadata
reads the current record under its write lock while preserving mapper hooks.
OpenWrite, Upload, and Delete acquire file-before-chunk locks, including caller
transactions and initial collection creation. Empty lazy Update batches use the
existing single-pass engine enumeration contract and never create missing
collections; incremental writes for missing file records are preserved.

Read snapshots still held by any cursor, including Include's secondary snapshots,
retain their page leases and WAL view through write upgrades and safepoints.
Commit, rollback, and error cleanup release them. Replacement snapshots acquire
their lock before the old write lock is released. Reacquiring a collection later
still sees the current transaction snapshot: an exact-baseline control proves
this is existing read-your-writes behavior, not whole-query snapshot isolation.

47 storage/transaction tests and 127 combined regression tests pass on net8.
Production targets build and net462 tests compile. Full net10 validation
(`p2-1344-integrated.trx`) has 1652 passed, 261 failed, and 8 skipped, with no newly
failing tests; original #1344 plus audit guards 42/185 now pass. Nine review waves
used four fresh independent Sol high agents each. Final three reviews were clean;
the fourth Include-reacquisition claim was refuted by the passing 67b214cc control.

## #1899 — retain collection includes in Query-based reads

Find(Query), including paging and FindOne, clones the query when collection
includes must be applied. The clone combines collection includes followed by
query includes, matching fluent query composition, while preserving caller-owned
query lists and pagination. Parent collection includes therefore resolve before
nested query includes. Reusing the caller query does not retain those additions.

Validation: original and two new baseline cases failed; final selection passes
10 tests including query composition and DbRef includes. Full suite: 1656 passed,
260 existing failures, 8 skipped; no previously passing regressions versus #1344.
Production and net462 builds pass. Two waves of four fresh Sol high reviewers;
wave one identified include dependency order, corrected with a nested-reference
regression. All four final reviewers (`review_1899_w2_a` through `_d`) were clean.

## #1851 — date aggregate projection and replay

Covered index queries reload date-bearing fields from the document using UTC
decoding and apply UtcDate to the projection. An internal factory retains the
ambiguous-DST flag of already-decoded dates. Scalar/nested sentinels and boundary
values retain document semantics. Non-date keys retain their fast path; index
encoding and comparison stay unchanged, preserving existing-file interpretation.
Address-based sort and aggregate replay correctly uses the cached data-block
address. This does not repair old lossy timestamps or incorrectly ordered indexes.

Validation: original baseline one failure / one pass; final 14 date cases pass in
New York and Auckland, 22 combined date/include/aggregate cases pass. Full suite:
1669 passed, 259 existing failures, 8 skipped, no regressions versus #1899.
Production and net462 builds pass. Four waves of four fresh Sol high reviewers:
addressed nested boundary decoding, existing-index compatibility, replay addresses,
and repeated-hour grouping. All four final reviewers (`review_1851_w4_a` through
`_d`) were clean.

## #1903 — registered deserializers after discriminator resolution

After resolving a document's _type and validating assignability, the mapper uses
an exact registered decoder for that resolved type. Declared-type decoders retain
precedence; decoder results, nulls and exceptions are returned without fallback.
The assignability guard runs before the resolved callback.

Validation: original baseline fails; five dispatch regressions and 54 combined
mapper/dictionary cases pass. Full suite: 1674 passed, 258 existing failures,
8 skipped, no regressions versus #1851. Production and net462 builds pass.
Four fresh Sol high reviewers (`review_1903_w1_a` through `_d`) were clean.

## #1904 — ordinary indexes and included reference payloads

The planner excludes ordinary indexes whose values can change when an included
reference is resolved. Affected predicates and sorting remain in the document
pipeline. Structural member-path analysis retains safe sibling indexes and
reference $id indexes through scalar, fixed/negative-position and wildcard-array
paths. Include now preserves the stub's $id against case-insensitive payload
collisions, making that identity-index optimization sound. Unrecognized computed
or dynamic shapes are conservatively excluded when their fields overlap.

Validation: original baseline fails; final 18 focused cases pass. Full suite:
1681 passed, 257 existing failures, 8 skipped; no regressions versus #1903.
Production and net462 builds pass. Four fresh four-Sol-high waves addressed safe
sibling/identity indexes, payload identity collisions and fixed array paths.
All final reviewers (`review_1904_w4_a` through `_d`) were clean.

A transient Index_With_Like full-suite failure was absent on the final run and
reproduced on unchanged pre-fix code under Turkish collation (3 instead of 4
matches); `/tmp/litedb-like-turkish-baseline-probe.log`. Earlier proposals to
expand root-Include scheduling and vector-index metric handling were scoped out:
root-Include filters fail even without an index on the unchanged baseline, and
the vector planner is a separate untouched path. Executed before/after controls
are `/tmp/litedb-include-probes-root-{baseline,candidate}.log`. This fix does not
claim to repair those inherited behaviors.

## #1920 — identities in standalone included-reference projections

Resolved references retain _id alongside $id, so direct/anonymous entity
projections preserve their identity without relying on the DbRef member hook.
Stub $type is also mapped to _type with the same precedence as the existing hook,
covering abstract/base references whose concrete target has no own discriminator.
The retained-$id guarantee from #1904 remains intact; stored stubs do not change.

Validation: original baseline fails; 36 reference/include cases pass. Full suite:
1683 passed, 257 failed, 8 skipped. The only extra failure was #1472 opening a
TempFile beside another test's orphan WAL before any query ran; both #1472 cases
pass isolated recheck. This is the known five-character temporary-name collision,
not a production regression. Production and net462 builds pass. Three fresh
four-Sol-high waves covered the initial fix, polymorphic metadata, and integration
with retained $id. All final reviewers (`review_1920_w3_a` through `_d`) were clean.

Missing/deleted-reference standalone projection behavior remains inherited and
is not claimed fixed. Before/after probe results are identical (normal DbRef hook
returns null; standalone projection creates a default entity), as recorded in
`/tmp/litedb-include-probes-{baseline,candidate}.log`. That broader hook-parity
proposal was refuted as outside this successfully resolved-target report.

## #1966 — read-only index rejection before WAL access

Scalar/vector EnsureIndex validates engine lifecycle and rejects ReadOnly with a
clear NotSupportedException before creating a transaction or write snapshot.
The scalar primary-key no-op remains unchanged. Rejected operations do not poison
the connection or create/write a WAL; subsequent Direct/Shared reads work.

Validation: all four original cases fail before the fix; 16 read-only cases pass,
including repeated scalar/vector rejection, byte preservation and disposed-engine
error precedence. Full suite: 1691 passed, 252 existing failures, 8 skipped; no
regressions versus #1920. The earlier #1472 temporary-name failure also passes in
this run. Production and net462 builds pass. Two fresh four-Sol-high waves;
addressed lifecycle error precedence. All final reviewers (`review_1966_w2_a`
through `_d`) were clean.

## Validation infrastructure — collision-resistant temporary filenames

TempFile now uses the complete GUID in both constructors instead of its first
five hex characters. The former 20-bit namespace collided with orphan WALs left
by intentionally failing tests, including inside fresh TMPDIRs; #1829 and #1920
validation encountered those false setup failures. Naming prefixes/extensions,
copy semantics and cleanup remain unchanged. Four fresh Sol high reviewers
(`review_tempfile_w1_a` through `_d`) were clean; 52 file-backed issue/reference/
transaction/read-only tests pass. No production behavior changes.

## #2052 — structured errors for invalid BSON dates

Current-format BSON date decoding validates milliseconds before DateTime
arithmetic and reports LiteException(INVALID_FORMAT) for out-of-range values.
Existing MinValue/MaxValue sentinel encodings and valid boundary dates remain
unchanged; input bytes are not modified. This does not reconstruct corrupt dates
or change the deferred legacy v4 reader/tick-encoded index format.

Validation: both original Int64-extreme cases fail before the fix; 23 focused
date cases pass, including first-invalid values, exact bounds and sentinels.
Full suite: 1700 passed, 250 existing failures, 8 skipped; no regressions versus
#1966. Production and net462 builds pass. Four fresh Sol high reviewers
(`review_2052_w1_a` through `_d`) were clean; removed an incidental BOM nit.

## #2033 — enumerable projection shapes and mapper hooks

Engine-created scalar/array envelopes carry internal, non-persisted shape metadata.
The base mapper unwraps those envelopes while preserving custom document encodings
and virtual ToObject hooks. Ordinary, grouped, aggregate and scored vector paths
retain the metadata through replacement read transforms. Array/list/enumerable
results preserve order, duplicates, empty values and terminal operation behavior.

Validation: all three original cases fail before the fix; 50 focused cases pass.
Full suite: 1713 passed, 246 existing failures, 8 skipped; no regressions versus
#2052. Production and net462 builds pass. Four fresh four-Sol-high waves addressed
scored projections, custom document encodings and ordinary/scored mapper hooks.
All final reviewers (`review_2033_w4_a` through `_d`) were clean. The unrelated
#2324 race happened to pass; it remains pending.

The proposed simple string/Guid custom-document expansion was refuted: baseline
and candidate both fail that inherited path (`/tmp/litedb-simple-projection-
{baseline,candidate}.log`). This fix preserves the existing simple-type contract
for custom engines. Byte arrays are not simple types and have explicit passing
custom document serializer coverage.

## #2113 — fluent grouped star projections

The grouped-query planner wraps enumerable selectors in ARRAY, matching SQL
SELECT semantics without mutating the caller's Query or its parameters. Group
members, HAVING filters, index use, query reuse and pagination remain intact.

Validation: original reproduction fails before the fix; 15 integrated focused
cases pass. Full suite: 1715 passed, 246 failed, 8 skipped. The only newly failing
case versus #2033 is the already-known intermittent #2324 mapper race; no other
regressions. Production and net462 builds pass. All four fresh Sol high reviewers
(`review_2113_w1_a` through `_d`) were clean.

## #2205 — integer literals outside Int64

The expression parser treats an integer token outside Int64 as its complete
string value, matching the report's requested string comparison. Quoted canonical
source preserves its sign and leading zeros and reparses consistently. Values
within Int32/Int64 keep their existing numeric type and behavior.

Validation: the overflow reproduction fails before the fix; 9 focused cases pass
including both bounds, leading zeros, indexed/SQL matching and canonical reparse.
Full suite: 1723 passed, 245 existing failures, 8 skipped; no regressions versus
#2113. Production and net462 builds pass. All four fresh Sol high reviewers
(`review_2205_w1_a` through `_d`) were clean.

## #2243 — missing IDs in reference collection schemas

DbRef list/array serialization now reports the declared element type's missing
_id mapping before accessing its getter, matching singular-reference diagnostics.
Null, empty and null-only reference collections retain their existing behavior.

Validation: the original interface-list reproduction fails before the fix; 10
focused cases pass, including arrays, diagnostics, batch rollback and reuse.
Full suite: 1726 passed, 244 existing failures, 8 skipped; no regressions versus
#2205. Production and net462 builds pass. All four fresh Sol high reviewers
(`review_2243_w1_a` through `_d`) were clean; removed an incidental BOM nit.
Nullable ID values remain a separate inherited path, not part of this missing
schema mapping fix.

## #2784 — foreign header validation before date decoding

The existing #2820 fix (`4cfed472`) also repairs both reproduced #2784 cases:
HeaderPage validates signature/version before decoding CreationTime, and the disk
service validates the header before any tail repair. Random and SQLite headers
with out-of-range ticks now report INVALID_DATABASE without changing input bytes.

Validation: 13 combined #2784/#2820 cases pass, including both original #2784
fixtures. All four fresh Sol high closure reviewers (`review_2784_w1_a` through
`_d`) confirmed the existing fix; no additional production change was needed.
Corruption inside an otherwise valid header remains outside these two cases.

## #2303 — constructor-bound members are populated once

Mapped parameter constructors decode their bound members and suppress only those
setters during subsequent base population. Unbound members still populate.
Per-instance metadata belongs to the active construction scope, preserving full
documents for virtual hooks and handling nested/decorated/reentrant factories.
Cleanup covers constructor/decorator failures and object/dictionary population.
Explicit, null, self-replacing and type-instantiator factories keep their existing
precedence and population behavior; missing fields retain default arguments.

Validation: original reproduction fails before the fix; 44 focused cases pass.
Full suite: 1740 passed, 243 existing failures, 8 skipped; no regressions versus
#2243. Production and net462 builds pass. Eight fresh four-Sol-high review waves
addressed Index fallback, full-document hooks, null/missing fields, decorated
factories, dictionary/construction cleanup, factory replacement and scope
ownership. All final reviewers (`review_2303_w8_a` through `_d`) were clean.

The proposed preservation of post-constructor member-decoder timing was refuted
as incompatible with correct constructor arguments after removing second setters:
DbRef/member decoders must run before construction for present bound fields. The
virtual hook remains post-construction and retains the original full document;
constructor and decoder side effects remain visible, as constructor side effects
already were. No input cloning contract is introduced.

## #2357 — reject nonexistent local dates before storage

BSON, raw-tick and index date writers explicitly reject nonexistent local hours
instead of silently converting them onto another valid UTC instant. UTC input,
valid local conversions and existing sentinel semantics are preserved. The file
format is unchanged; callers storing wall-clock labels must choose an explicit
representation rather than allowing a nonexistent local instant to collapse.

Validation: the reporter-kind process reproduction emits BUG_2357_CONFIRMED on
baseline and VERIFIED_2357 with the fix. Its UTC controls, full receipt ledger,
rollback, subsequent writes and reopen pass. 28 focused date tests pass under
America/New_York and UTC. Full suite: 1744 passed, 243 existing failures, 8 skipped;
no regressions versus #2303. Production and net462 builds pass. All four fresh
Sol high reviewers (`review_2357_w1_a` through `_d`) were clean.

## #2549 — publish caller-owned streams on rebuild

Rebuild checkpoints caller-owned streams under the existing exclusive gate before
returning, leaving the stream and engine usable for subsequent writes. It does
not replace or dispose caller-owned storage. Active transactions remain rejected.

Validation: original reproduction fails before the fix; 32 focused cases pass,
including repeated plain/encrypted publication, ownership and rollback. Full
suite: 1749 passed, 241 existing failures, 8 skipped; no regressions versus #2357.
The unrelated #2324 race happened to pass and remains pending. Production and
net462 builds pass. All four fresh Sol high reviewers (`review_2549_w1_a`
through `_d`) were clean.

## #2790 — release query leases after thread handoff

Each transaction owns a counted read lease that cursor cleanup can release on
another thread. Removing its registry entry releases that lease exactly once;
other readers and foreign explicit transactions retain their own leases.
Exclusive operations retain thread ownership and writer preference.

Validation: original reproduction fails before the fix; 43 focused cases pass,
including foreign disposal/completion, duplicate disposal, await and Parallel
query handoffs. The integrated broad transaction filter passes 97 tests with two
known failures (#2822 and SQL trailing-clause validation). Full suite: 1755 passed,
239 existing failures, 8 skipped; only the known intermittent #2324 outcome
varied adversely versus #2549. #1834 also passes and awaits a separate closure
audit. Production/net462 builds pass. All four fresh Sol high reviewers
(`review_2790_w1_a` through `_d`) were clean.

## #2792 — capture a coherent snapshot publication boundary

Snapshot creation reads the WAL version and collection page ID together under
the header monitor used by commit publication. It loads the page after releasing
that monitor. Its admitted transaction prevents checkpoint clearing meanwhile.

Validation: the new controlled publication-gap regression fails on baseline;
19 integrated focused cases pass. The dedicated reader thread must actually
block before the paused writer can publish, avoiding a scheduling-only signal.
Full suite: 1756 passed, 239 existing failures, 8 skipped; no regressions versus
#2790. Production/net462 builds pass. Reviews narrowed the critical section to
metadata and strengthened the thread handshake. Four fresh final Sol high reviews
(`review_2792_w4_a` through `_d`) were clean; wave 4 replaced a partially failed
review wave after reviewer service disconnections.

## #2583 — order by explicit SQL projection aliases

Standalone deterministic explicit aliases resolve to their retained projection
expressions, including document and literal-word aliases. Explicit source paths,
inferred names, groups, source aggregates and volatile aliases retain their
existing interpretation. Names are unique under the document's case-insensitive
comparison. Computed aliases use exact parsed constants through nested expression
delegates and retain a sorter when a display expression could collide with an
index; plain paths retain cache and index-streaming optimizations.

Validation: both original ordering reproductions fail before the fix; 32 integrated
focused cases pass, covering precision/cache collisions, nested MAP, parameters,
multiple columns, emitted names and index plans. Full suite: 1783 passed, 236
existing failures, 8 skipped; no regressions versus #2792. Production/net462 builds
pass. Nine fresh review waves addressed precision, naming, index streaming and
compilation cost. All four final Sol high reviewers (`review_2583_w9_a` through
`_d`) were clean. Computed alias compilation deliberately avoids the lossy display
Source cache; nested instances compile exactly once per parsed expression.

## #2821 — explain closed-engine recovery after I/O failure

The operation encountering an I/O failure retains its original exception.
Subsequent operations report that the engine is closed and must be disposed and
reopened, with the original cause retained as InnerException. First-cause atomic
publication and non-I/O failure identity remain unchanged. Existing lifecycle
fixes already repair the reproduced initialization-handle leak and recovery path.
WAL files remain intact; the message never instructs callers to delete them.

Validation: the original diagnostic case fails before the fix; 130 focused
recovery/concurrency/WAL cases pass, including unchanged first-operation errors
and updated subsequent-error contracts. Full suite: 1784 passed, 235 existing
failures, 8 skipped; no regressions versus #2583. Production/net462 builds pass.
Four fresh final Sol high reviewers (`review_2821_w2_a` through `_d`) were clean.

## #2798 — resolve declared interface/abstract IDs from known schemas

Query translation infers ID storage fields from successfully initialized concrete
schemas already known to that mapper, requiring agreement. Explicit field/ID
configuration and actual custom ID-selection overrides take precedence. Matching
uses CLR interface/override slots, exact generic contracts before variance, and
covariant override identity rather than hidden names. Failed/incomplete schemas
are excluded without waiting or mutating the declared mapping.

A fresh mapper must register concrete Entity<T> schemas or map concrete values
before this inference is available; there is no assembly or persisted-schema scan.

Validation: both original query/delete cases fail before the fix; 34 integrated
mapper/constructor cases pass. Full suite: 1804 passed, 233 existing failures,
8 skipped; no regressions versus #2821. Production/net462 builds pass. Seven
fresh four-Sol-high review waves covered initialization, explicit provenance,
slot identity and custom mapper policy. Final reviewers (`review_2798_w7_a`
through `_d`) were clean. A claimed reflection ambiguity for a covariant mapper
override was refuted by an executed net8 regression probe, retained as a test.

## #2847 — honor explicit CLR string comparison modes

LINQ string.Equals overloads with StringComparison use dedicated BSON functions
for all six CLR comparison modes, with both enum encodings and correct static/
instance null behavior. Culture-sensitive expressions cannot become persistent
indexes. Plain field indexes do not replace explicit comparison semantics.
The static no-mode overload now translates its actual two operands.

Validation: original Ordinal cases fail before the fix; 32 integrated focused
cases pass, including Turkish-culture CLR oracles, index presence, null behavior,
static no-mode calls and expression-index rejection. Full suite: 1812 passed,
231 existing failures, 8 skipped; no regressions versus #2798. Production across
all targets and net462 builds pass. Four fresh Sol high reviewers
(`review_2847_w1_a` through `_d`) found only valid coverage nits, now addressed,
and a false claim that enum relational operators do not compile. Actual successful
production/net462 builds and executed mode tests refute that claim.

## #2822 — reject completion on the wrong transaction thread

Commit/Rollback diagnose an active explicit transaction owned by another thread
instead of silently returning false. Explicit ownership is published only for a
successful new BeginTrans; false joins to automatic transactions remain automatic.
Shared completion holds the named mutex through cleanup, preserves live foreign
owners, and discards abandoned uncommitted state with a diagnostic even when
another instance consumed the abandonment signal. Public disposal then works.
The API remains synchronous and thread-bound; await and logical-task identity
require a separate transaction-handle design. Nested false joins are not savepoints.

Validation: original reproduction failed before the fix; 59 focused concurrency,
storage, abandonment and bulk-lifecycle cases pass. Existing test cleanup now
rolls back only its still-owned transaction, not an already committed transaction
while a worker owns another. Full suite: 1831 passed, 228 existing failures,
8 skipped; no new failures versus #2847. Production/net462 builds pass. Eight
fresh four-Sol-high review waves addressed shared recursion, publication,
abandonment and false automatic joins. Final reviewers (`review_2822_w8_a`
through `_d`) were clean.

## #2812 — reject incompatible collation environments before queries

New files record a non-cryptographic collation fingerprint in reserved header
bytes. Opening validates both the data header and recovered WAL header. Ordinal
stamps are independent of runtime sort tables. Legacy zero-stamp files (including
older Mono without SortVersion support) scan every ordinary index's stored order
and unique equivalence before queries, without adopting a stamp or rewriting data.
Structural corruption keeps its existing diagnostic and explicit recovery path.

Healthy mismatches require an original-environment Ordinal rebuild or export/import.
Explicitly requested AutoRebuild of a flagged damaged file retains its established
recovery behavior before validation, including ReadOnly opens; duplicate failures
abort before replacement. Old writers ignore stamps, and the 32-bit fingerprint
is not an integrity guarantee. See docs/collation-runtime-compatibility.md.

Validation: 14 isolated focused tests pass; full isolated suite has 1824 passes,
231 existing failures and 8 skips, with no new failures against #2847. Combined
with the pending AOT fix: 1856 passes, 228 existing failures, 8 skips, no new
failures against #2822. Production/net462 builds, ordinary/encrypted v8 compatibility,
and both package/source process variants pass their controls. The source rejects
both incompatible process directions while preserving the complete ledger.
Four fresh final Sol high reviewers (`review_2812_w3_a` through `_d`) were clean.

## User-directed v4 deferrals

The user requested skipping v4 work and keeping reviewed candidate a13746f5
unpublished. In addition to #2808, legacy upgrade reports #2823, #2826 and #2855
are deferred. No v4 fast-forward, stable tag, NuGet publication or advisory change
is authorized by this sweep.

## #2804 — evaluate expressions on runtimes without dynamic code

Runtime capability checks and actual custom-delegate compilation/invocation
probes select static adapters plus an interpreter when dynamic code is unavailable.
Explicit type initializers guarantee that the public LiteDB.UseInterpreter startup
switch is checked after first use; older hosts with native probe failures can
bypass probing. Normal JIT execution keeps compiled expressions.

The fallback covers BSON parser/mapper shapes and supported captured values,
including nullable/lifted conversions, invocation, list initialization and CLR
argument/exception order. General CLR trees, including nested captured LINQ
selector/predicate lambdas, explicitly require precomputation. A retained test
verifies both that diagnostic and the working precomputed query. Trimming still
requires preserving mapped metadata. See docs/aot-runtime.md.

Validation: focused AOT/collation selection passes 28 cases, including hundreds
of compiled-CLR numeric oracles. Final integrated suite with the pending #2859
candidate: 1875 passed, 220 existing failures, 8 skipped; no new failures versus
#2822. Production/net462 builds pass. Package 5.0.21 still reproduces; source
passes Linux NativeAOT and Mono full-AOT controls plus mapping/query/index oracles.
A fresh Mono JIT process checks the public startup switch and runtime selection.
Post-release captured-query probes are source-only; original package controls
remain intact. Apple/Unity device toolchains were not exercised.

Ten fresh four-Sol-high review waves addressed evaluation order, lifted/coalesce
conversions, actual delegate capability detection and guaranteed startup ordering.
Final reviewers (`review_2804_w10_a` through `_d`) were clean. Claims that boxed
underlying values cannot be used as nullable-typed Expression.Constant operands
were refuted by executed .NET and Mono probes, passing regression/process oracles,
and an independent final reviewer probe. Extending the bounded interpreter into
a general nested-lambda CLR compiler was declined; the limitation and workaround
are explicitly documented and tested instead.

## #2859 — propagate collation through nested BSON and query comparisons

Array/document comparisons recurse using the supplied collation; parameterless
comparison remains Binary. Cross-type results retain the normalized sign needed
by index traversal. Scalar range predicates, indexed NotEqual and inclusive range
boundary handling now agree with the configured comparer. Nested values can be
index keys, contrary to the issue's assumption, so non-Ordinal fingerprints gain
a comparer revision and zero-stamp files validate their actual stored ordering.

Validation: original four cases and the added range/NotEqual/mixed-type oracles
fail before their fixes. Final isolated selection: 35 passes. Integrated full
suite: 1885 passed, 219 existing failures, 8 skipped; no new failures. Production/
net462 builds pass. An actual two-version file probe creates files with the prior
fingerprint implementation: new code rejects the old non-Ordinal stamp, opens
the old Ordinal file with the exact ledger, and preserves both files' bytes.

Four fresh review waves addressed index paths, scan consistency and cross-type
sign normalization. Final reviewers (`review_2859_w4_a` through `_d`) had no
unresolved valid new defects. Duplicate IN/multikey row claims were refuted by
executed controls and Index.Run's existing DataBlock DistinctBy; unique multikey
indexes remain explicitly unsupported. H16's different-field document comparator
law violation predates this patch and remains an existing full-suite failure;
changing document field identity/order is outside this collation propagation fix.

As with #2812, reserved v8 metadata cannot stop an older writer preserving the
stamp while changing index semantics. Documentation now explicitly prohibits old
version access to non-Ordinal nested indexes, even with identical culture/options,
and explains the Ordinal migration path. A matching stamp does not detect that
unsupported downgrade; no such guarantee is claimed.

## #2450 — retain one current rebuild backup

Explicit Rebuild replaces the canonical -backup file with the checkpointed
pre-rebuild snapshot. Older numbered backups and unrelated files remain intact.
Recovery and upgrade rebuilds retain their existing unique-backup behavior.
Atomic replacement failure preserves the committed original database.

Validation: both original regressions fail before the fix; 19 isolated focused
cases and 55 integrated cases pass, including encrypted Direct/Shared rebuilds
and failed replacement. Combined with pending #1162, the full suite has 1896
passes, 216 existing failures and 8 skips, with no new failures versus #2859.
Production and net462 compilation pass. File.Replace's documented existing-backup
contract was checked; Windows runtime execution was not performed locally.

Four fresh Sol-high reviewers (review_2450_w1_a through _d) found no substantive
defects. Their test temporary-file cleanup nit was addressed and all five issue
cases passed again.

## #1162 — map ExpandoObject dictionaries as documents

Expando values declared as object, Expando or dictionaries now serialize their
key/value fields into BSON documents; typed Expando reads populate those fields.
Explicit IEnumerable/ICollection of pairs retains its existing array contract.
Custom handlers and depth limits retain priority; case-insensitive BSON key
collisions reject input instead of losing values. A sealed typed Expando treats
_type as data. Untyped object values retain the existing mapper discriminator
convention used by ordinary dictionaries; this is not an arbitrary-JSON decoder.

Validation: original reproduction and added collision, enumerable-contract and
typed _type cases fail before their corresponding fixes. Final focused selection
passes 50 cases. Full suite: 1898 passed, 216 existing failures, 8 skipped, with no
new failures versus #2450's combined validation. Production/net462 builds pass.
The #1376 legacy-array guard now inserts the legacy raw array directly, preserving
its rejection oracle after Expando's serialization shape was corrected.

Four fresh review waves addressed key collisions, declared collection contracts
and typed _type payloads. Final reviewers review_1162_w4_a through _d were clean.

## #2846 — correct asynchronous versus completed-write comparison

The return-latency difference reproduces, but the compared boundaries differ:
5.0.9 commits enqueue background WAL writes and return before their durable
flush, while current commits finish that work before acknowledgment (#2818).
Actual old-source inspection and a probe finding about 35,000 still-queued pages
at return establish the difference. Restoring early acknowledgment would undo
the durability fix.

The benchmark retains return medians as telemetry and compares completed writes.
A version-checked legacy adapter waits for the writer and performs an observable
Flush(true), because that worker suppresses IOExceptions. No queue diagnostic
work remains inside timing. A data/WAL copy taken before Dispose must recover the
drop and sentinel independently; original reopen and collection-reuse controls
remain intact.

Final integrated three-sample comparison at 100,000 rows: package return/complete
162.288/510.868 ms; source 507.102/507.180 ms. Return ratio 3.125; completed ratio
0.993. Earlier independent completed ratios were 0.919, 0.974 and 1.058. This does
not establish the original 2.7 GB timing. No engine patch was retained. The
completed-work slowdown is not currently reproduced at the retained scale.

Two fresh four-Sol-high waves audited the measurement correction. Observable
legacy flush failures and removing asymmetric diagnostic overhead were addressed.
A request to retain the old mismatched return-ratio assertion was declined:
current synchronous return is already bounded by the completed-work threshold,
and its raw return ratio is explicitly telemetry. Final reviewers
review_2846_w2_a through _d were clean.

## #1834 — shared fix for deferred-query thread handoff

The retained candidate reproduction is cofixed by #2790 (90a5f363): foreign
cursor disposal releases the transaction creator's counted read lease exactly
once. The owner can query again and an exclusive checkpoint can proceed. The
unchanged Issue1834 test passes in the current full suite, including its independent
LINQ and exact-row oracles. Four fresh Sol-high closure reviewers
review_1834_w1_a through _d were clean. No new engine patch is needed.

This resolves the reproduced handoff defect, not the incomplete Xamarin/iOS
report's unknown trigger. No device execution or platform-specific fix is claimed.

## #2849 — reuse idle cache frames for writes

A write now takes ownership of an idle cached page instead of allocating and
copying another frame. The cache removes its readable entry and shared-read hint
under the cache lock, updates accounting, and advances the frame generation.
Pinned readers retain their original page and force a copy. New concurrent
readers load the committed disk version. Transaction page budgets, safepoint
frequency and durable WAL ordering are unchanged.

The retained parameterized million-row production benchmark, run without other
builds or benchmarks, passes all three alternating sample pairs and all six
complete data checks. Insert medians are 32,103/17,518 ms (default/large budget),
ratio **1.833**; delete medians are 25,559/15,470 ms, ratio **1.652**. The preceding
unmodified source measured 2.813/2.303. These are Linux x64/net8.0 measurements;
no post-fix Windows performance result is claimed.

Ownership, stale shared hints, pinned readers, commit/rollback, encryption and
pre-disposal WAL recovery are covered. Validation: 82 focused tests; 49 integrated
checks; full suite 1905 passed, 216 existing failures, 8 skipped, with no new
failures. Production and net462 builds and plain/encrypted vector compatibility
pass. Four fresh independent Sol-high reviewers found no actionable issues.

Final reviewers: review_2849_w1_a through _d, all clean.
