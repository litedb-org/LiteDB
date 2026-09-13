# Vector query semantics and file compatibility

Fixes [upstream issue #2881](https://github.com/litedb-org/LiteDB/issues/2881).

## Query planning

Ordinary predicates, projections, grouping, and ordering consider only ordinary
skip-list indexes (`IndexType == 0`). Vector indexes have their own access path.
`VECTOR_SIM(field, target)` and `field VECTOR_SIM target` both expose their
operands to the vector planner. SQL `VECTOR_SIM` means cosine distance
`1 - dot(a, b) / (length(a) * length(b))`; a SQL expression cannot use an index
configured for a different metric. Explicit `WhereNear` and `TopKNear` calls
continue to use the selected vector index's metric (including minimum similarity
for dot product).

A finite nearest-neighbor query may use approximate HNSW top-k search when it has
no offset, residual filters, includes, or grouping, and its complete ordering is
provided by that search. This remains approximate: the graph's candidate search
does not guarantee exact nearest-neighbor recall. A vector threshold query without
an explicit sort likewise permits this bounded search when it has a finite limit.

Queries with residual operations, offsets, or no finite limit evaluate all valid
vectors through the collection's primary index. They do not inherit HNSW's default
32-candidate ceiling. They apply filters, grouping, requested sorting, offset, and
limit in the normal pipeline. Sorting is removed only for a single ascending
vector key that matches the selected field and target. Descending order, secondary
keys, and an independent vector target retain their requested sort.

Complete evaluation loads and ranks the matching documents, so these query shapes
can require O(N) memory and O(N log N) ranking work. Simple bounded top-k queries
retain the existing ANN performance characteristics. Equal-distance neighbors
have no specified relative order unless the query supplies a tie-breaker.

## File format 9

New databases, including databases that have not yet stored vectors, use header
format version **9**. Normal opening requires version 9. The BSON vector type,
vector index pages, and vector collection metadata therefore cannot be written
into a newly created file declaring the legacy version 8 format.

Migrate existing version 7 or 8 files explicitly with `Upgrade=true` on a writable
file connection. Migration rebuilds into a new version 9 file and retains the
original data and WAL as backup files. Documents, ordinary indexes, vector index
metadata, and encryption are preserved. An encrypted migration requires the
existing password. Reopening a current file with `Upgrade=true` does not migrate
it again. Read-only connections cannot migrate; prepare a migrated copy with a
writable connection first. Custom streams require migration through a file copy.

Released engines that require header version 8 reject version 9 at open, before
queries, writes, or rebuild. This cannot retroactively protect vector files
already created by development builds under version 8: migrate those files and
keep their backups away from older engines. Do not change a database's version
byte as a substitute for migration.

## Validation

`Issue2881_VectorPlanning_Tests` compares indexed results with a full-scan cosine
reference, including more than 32 documents, filtering, offsets, strict bounds,
grouping, descending ordering, and distance ties. The initial test-only commit
also corrects an existing test that asserted the broken removal of secondary sort.
Its focused run had 22 failures and 9 passing regressions before the fix.

`Issue2881_VectorFormat_Tests` checks version gating, source preservation, vector
rebuild, and plain/encrypted migration. Existing recovery fixtures adjust only
their disposable copies' version headers so their recovery conditions survive.

Run `python3 scripts/test-vector-compatibility.py` for the cross-version check.
It uses separate processes for the current library and NuGet LiteDB **5.0.21**:
plain and encrypted vector files are rebuilt by the current engine, refused
without data-file changes by the old engine in read/write/rebuild/upgrade modes,
and ordinary files created by the old engine migrate successfully. Linux CI runs
this check alongside the unit tests.
