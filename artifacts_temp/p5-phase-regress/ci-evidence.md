# Hosted CI evidence ledger

Workflow success is distinct from valid report-only measurements. Historical failures and superseded runs are retained in the step log; no failed workflow is called green.

|Run|Purpose|Harness SHA|Status / conclusion|Active matrix jobs: success / failure / queued / running|
|---|---|---|---|---|
|[run42541251747](https://github.com/litedb-org/LiteDB/actions/runs/42541251747)|24 adjacent-phase timing legs; obsolete post-timing trace CLI failures|568a4cb8316271f2cbe21417d481500ab4725fee|completed / failure|22 / 2 / 0 / 0|
|[run42541435160](https://github.com/litedb-org/LiteDB/actions/runs/42541435160)|14 introducing/control timing legs; obsolete post-timing trace CLI failures|6919988ecf0594475d8826467202ba5830f3bff4|completed / failure|9 / 5 / 0 / 0|
|[run42544497136](https://github.com/litedb-org/LiteDB/actions/runs/42544497136)|3 primary diagnostic pairs; exact0b and optional raw-drain production-build failures|c5b44a2bd4ca5964050f7314f5dad0b1ff0e83e7|completed / failure|3 / 2 / 0 / 0|
|[run42547323587](https://github.com/litedb-org/LiteDB/actions/runs/42547323587)|2 net8 writer diagnostic pairs|1fd207a7ef1516ac2d00ca6a948d304ac72db8fb|completed / success|2 / 0 / 0 / 0|
|[run42548192227](https://github.com/litedb-org/LiteDB/actions/runs/42548192227)|1 integrated Phase3/Phase4 net8 writer diagnostic pair|1fd207a7ef1516ac2d00ca6a948d304ac72db8fb|completed / success|1 / 0 / 0 / 0|
|[run42550856850](https://github.com/litedb-org/LiteDB/actions/runs/42550856850)|2 buildable reader activation timing legs and1 diagnostic job (scan +open control)|1fd207a7ef1516ac2d00ca6a948d304ac72db8fb|queued / pending|0 / 0 / 3 / 0|

All runs use unmodified before/after product commits. Benchmark checksums and graceful reopen are diagnostic integrity checks; no new product safety qualification or fixed-binary gain is claimed. No local benchmarks ran.
