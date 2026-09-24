# Working on LiteDB

Data integrity comes first. Avoid forced legacy rebuilds; upgrades must be atomic,
repeatable, and recoverable after crashes or power loss. Read
[data safety](docs/rules/data-safety.md) before changing storage or migration code.
Implementation is not complete until tests establish database safety for the affected
behavior. Unresolved safety risks or missing safety coverage block completion.

Read [development](docs/rules/development.md) and
[workflow](docs/rules/workflow.md) before changing code. Read the relevant topic
below when working in that area; there is no need to load every rule file.

| Work area | Guidance |
| --- | --- |
| Reproductions, tests, fuzzing, CI evidence | [Validation](docs/rules/validation.md) |
| File formats, upgrades, persisted indexes | [Compatibility](docs/rules/compatibility.md) |
| Transactions, cursors, WAL, disposal, buffers | [Storage and ownership](docs/rules/storage-ownership.md) |
| LINQ/SQL translation, expression and statement caches | [Query expressions](docs/rules/query-expressions.md) |
| Index planning, INCLUDE, sorting, LIKE, vectors | [Query execution](docs/rules/query-execution.md) |
| BSON, mapper contracts, public API behavior | [Mapping and serialization](docs/rules/mapping-serialization.md) |
| Benchmarks and memory measurements | [Performance](docs/rules/performance.md) |
| Source generators, trimming, AOT | [Code generation](docs/rules/code-generation.md) |

The library is in `LiteDB/`; tests are in `LiteDB.Tests/`. Use `gh` for GitHub.
Issues and PRs normally belong to `litedb-org/LiteDB`; verify the actual PR head
repository before pushing. New work normally targets `dev`.

Keep this file as an entry point. Add durable lessons to the relevant rule file,
prefer links to existing design docs and tests, and remove superseded guidance.
Do not add task logs, individual bug histories, or benchmark results here.
The [session review](docs/rules/session-review.md) records the sources and limits
of the initial cleanup.
