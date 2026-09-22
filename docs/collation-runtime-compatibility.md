# Persisted index ordering and runtime compatibility

PR #2924 changes persisted comparison rules. Existing skip lists can produce
incorrect seeks and ranges under the new comparer even though their page layout
is readable. This requires a file-format boundary, not just a package version bump.

New databases use **format v11**, including databases without vectors. Writable
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

The engine then durably promotes the persisted data header to v11 **before** any
migration pages enter the WAL. It reorders primary and proven scalar member-path skip lists while preserving
node/data addresses and document index chains, without allocating replacement pages.
It clears and regenerates computed and multikey BSON indexes from documents,
reusing their pages and retaining their sentinels, names, expressions, and uniqueness. This is conservative: a stored
key's type cannot prove that a computed expression or multikey distinctness is unaffected. Sorting old keys alone could permanently
omit values collapsed by the old equality rules. Simple member-path vector indexes
are retained; computed vector indexes are regenerated with their dimensions and
metric because comparison changes can also affect their input expressions.

All index changes and the completed ordering revision (header byte 165, currently
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

### Databases with a finite size limit

Before promotion, migration calculates a conservative page budget for regenerated
indexes, crediting reusable index pages and the valid database free-page list.
Insufficient `LIMIT_SIZE` rejects the open without writing migration changes, so
the original engine can still open a legacy file. Scalar member-path indexes can
migrate at the existing limit because they reorder in place.

The error reports an upper-bound budget in bytes. Retry with, for example,
`filename=data.db;index migration limit size=256MB`, or set
`ConnectionString.IndexMigrationLimitSize` / `EngineSettings.IndexMigrationLimitSize`
to that byte budget. The requested limit must be at least the stored limit and
logical database size. This option applies only to pending index migration and
persists the increased `LIMIT_SIZE` in the same successful transaction as the
indexes. It also recovers v11 files left pending by an earlier migration attempt;
failed or unconfirmed transactions do not persist the new limit.

Worst-case skip-list heights make this budget larger than typical actual growth;
empty pages are reused during regeneration. This is a logical file-size check,
not a reservation of physical disk space for database, WAL, or temporary sorting.

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
`Rebuild()`. New/migrated v11 files are rejected by 5.0.21 and earlier v8/v9 readers;
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
collation, checkpointed data/confirmed WAL, and finite-limit rejection/recovery.
The `Index migration compatibility` CI workflow exchanges these fixtures between
Windows with NLS explicitly enabled and Linux/ICU, in both directions, and checks
cultural string order and seeks after migration. Generated fixtures are not a
substitute for a customer-data migration rehearsal.

`IndexMigration_Tests` also covers v9 vector metadata, low transaction page limits,
updates/deletes after migration, read-only behavior and interrupted promotion.
`IndexCompatibilityValidation_Tests` covers mismatch rejection before tail repair
for data/WAL, plain/encrypted, and read-only/writable combinations. Promotion
failure tests cover caller streams and durable-flush failures through encryption.

Run `python3 scripts/test-index-migration-recovery.py` for abrupt process death and
partial I/O failures using real plain/encrypted files. It interrupts promotion,
unconfirmed WAL, confirmed commit, and checkpoint, then verifies every computed
and multikey lookup, updates/deletes, checkpoint, and read-only reopen in a fresh
process. Partial encrypted writes are injected below encryption. These tests
exercise process failure and partial page writes, not hardware power-loss behavior.

Run `python3 scripts/measure-index-migration.py --documents 100000` for production
assembly measurements of actual 5.0.21 migration with scalar, computed and multikey
indexes, in plain/encrypted files. It reports first-open/checkpoint time, allocated
bytes, peak process working set, final file size and WAL size at open completion,
plus combined database/WAL/temp size sampled every 10 ms (a lower bound on peak
disk use). It also compares numeric
operation costs against 5.0.21. Run it separately from builds using test hooks.

One local Linux/.NET 8 run with tiered compilation disabled, 100,000 documents
and four indexes produced the following measurements (decimal MB):

| File | Source | First open | Peak process memory | Sampled peak disk | Final growth |
| --- | ---: | ---: | ---: | ---: | ---: |
| Plain | 110.0 MB | 5.48 s | 143.8 MB | 204.2 MB | 16 KB |
| Encrypted | 110.0 MB | 5.89 s | 158.1 MB | 204.2 MB | 16 KB |

Both runs allocated about 10.8 GB cumulatively, reclaimed by GC. These numbers
are a reproducible synthetic baseline, not a latency or memory guarantee for
other data or hardware. For 200,000 repeated operations, integer hashing took
4.0 ms with no per-operation allocation (5.0.21: 1.3 ms). Exact double/decimal
comparison of `0.1` took 80 ms and allocated 33.6 MB (5.0.21: 8.0 ms, no
per-operation allocation, but incorrect equality). Mixed numeric hot paths
therefore retain a measurable correctness cost; use representative workload
measurements when sizing an upgrade.
