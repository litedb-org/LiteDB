# Persisted index ordering and runtime compatibility

PR #2924 changes persisted comparison rules. Existing skip lists can produce
incorrect seeks and ranges under the new comparer even though their page layout
is readable. This requires a file-format boundary, not just a package version bump.

New databases use **format v10**, including databases without vectors. Writable
opens of v8/v9 databases automatically migrate their indexes before exposing the
connection. Read-only opens requiring migration fail with instructions to open
writable once. `Upgrade=true` retains the separate v7 rebuild/backup path, including
explicit v7 upgrades requested together with `ReadOnly=true`.

## Automatic migration

The engine restores confirmed WAL transactions, validates index chains, evaluates
index expressions from the original BSON documents, and checks all unique keys
with the current comparer. Unique-key collisions or expression errors stop this
preflight before data/WAL mutation. Resolve collisions in the original compatible
environment; migration never silently removes documents.

The engine then durably promotes the persisted data header to v10 **before** any
migration pages enter the WAL. It reorders primary and proven scalar member-path skip lists while preserving
node/data addresses and document index chains, without allocating replacement pages.
It clears and regenerates computed and multikey BSON indexes from documents,
retaining their sentinels, names, expressions, and uniqueness. This is conservative: a stored
key's type cannot prove that a computed expression or multikey distinctness is unaffected. Sorting old keys alone could permanently
omit values collapsed by the old equality rules. Simple member-path vector indexes
are retained; computed vector indexes are regenerated with their dimensions and
metric because comparison changes can also affect their input expressions.

All index changes and the completed ordering revision (header byte 109, currently
1) commit in one transaction. Documents, user version, collation, encryption and
other pragmas are retained. Revision zero means migration is still required,
including after interrupted promotion or an unconfirmed migration transaction.
Old WAL headers cannot lower the file version. Normal transaction WAL replay
publishes either all rebuilt indexes or none; a retry rebuilds from the last
committed documents. The durable version barrier remains after a failed migration.
As with other full-page writes, torn header writes may require explicit recovery.

Migration uses the temporary external sorter and transaction safepoints; it needs
temporary disk/WAL space and can make the first writable open expensive. It does
not create a whole-database backup. Subsequent opens use the revision and comparer
stamp instead of repeating migration. Keep a backup before upgrading production
files, and rehearse large migrations and unique constraints on representative copies.

## Comparer stamp and runtime changes

Header bytes 92–95 contain a 32-bit compatibility fingerprint covering comparison
options, culture, runtime sort-version/ID, comparison probes, and BSON ordering
revisions. Ordinal comparison has a runtime-independent fingerprint. Unknown
nonzero fingerprints are rejected before trailing-page repair or query/write
access, including fingerprints recovered from WAL. They are not silently adopted
as part of automatic legacy migration. The fingerprint is not an integrity check.

Runtimes without sort-version support use zero and validate stored level-zero
ordering on each open after migration. Explicitly requested `AutoRebuild=true`
recovery of a file marked corrupt retains its existing recovery semantics and
backup/error-report behavior. It can run before compatibility rejection.

For a fingerprint mismatch, export with the original compatible engine and import
with this engine. For culture-only differences, an Ordinal rebuild in the original
environment can also prepare a portable file. A rejected connection cannot call
`Rebuild()`. New/migrated v10 files are rejected by 5.0.21 and earlier v8/v9 readers;
do not edit the version byte to bypass that boundary.

## Changed comparisons

- Nested arrays/documents recursively use the execution collation.
- ObjectId timestamp and PID bytes compare unsigned, including across the 2038
  timestamp boundary. Public signed properties retain their raw bits.
- Documents compare case-insensitive field names in canonical ordinal order,
  then corresponding values. Differently named null fields are distinct.
- Mixed numbers compare their exact represented binary/decimal values. Binary64
  `0.1` is greater than decimal `0.1`; exactly represented values such as `0.5`
  remain equal across types. NaN sorts below numbers, and numeric hashes preserve
  exact cross-type equality.

## Validation

Run `python3 scripts/test-index-compatibility.py` to produce real 5.0.21 fixtures
and test current migration, indexed/scan agreement, exact parameterized seeks,
regenerated multikey/computed keys, external sorting, collision nonmutation, and
old-engine rejection. The matrix covers plain/encrypted files, binary/culture
collation, and checkpointed data/confirmed WAL. Generated fixtures are not a
substitute for a customer-data migration rehearsal.

`IndexMigration_Tests` also covers v9 vector metadata, low transaction page limits,
updates/deletes after migration, read-only behavior and interrupted promotion.
`IndexCompatibilityValidation_Tests` covers mismatch rejection before tail repair
for data/WAL, plain/encrypted, and read-only/writable combinations. Promotion
failure tests cover caller streams and durable-flush failures through encryption.
