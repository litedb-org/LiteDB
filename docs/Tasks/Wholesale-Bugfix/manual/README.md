# Manual sweep (2026-09-16)

The user's latest instructions supersede the hosted execution plan: manually fix
reproduced reports from PR #2885 in live priority-label order, obtain four
independent GPT-5.6 Sol reviews, resolve justified findings, then commit and push
each fix before selecting the next. P1 precedes P2 and P3.

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
