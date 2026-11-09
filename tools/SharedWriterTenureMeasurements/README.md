This production harness measures Phase 3-A on fork CI only. `run.py` refuses
execution unless CI=true and GITHUB_REPOSITORY=JKamsker/LiteDB. The workflow builds
the same candidate harness against Phase 2 commit 7d9975e30 and the candidate
library with TestingEnabled=false, runs five serial alternating pairs on Linux
x64/net8/net10, and retains raw samples, paired ratios and commit/runtime identity.

Single-process automatic small updates report throughput and p50/p95 latency.
Two preattached processes alternate updates through parent pipe handshakes and
report operation latency separately from total wall time. DurableCommits=false
and durable defaults are separate samples. Every measured database is cold
verified for its final row, secondary index, payload and unrelated sentinel.
No threshold or speedup is inferred before the hosted artifacts are available.

Dispatch benchmark-gate.yml on JKamsker/LiteDB with shared_tenure_only=true,
candidate_repository=litedb-org/LiteDB and candidate_ref=<Phase 3-A SHA>, using a
workflow ref containing this job. No local benchmark command is provided.

The separate `shared_newcomer_only=true` job measures newcomer acquisition on
Linux x64/net8/net10 against pre-fix df6b16f80 (override `baseline_ref` explicitly
when needed). Two OS processes alternate roles; the incoming process has no
mapped facade/table reference until its timed attach. The timer starts before
SharedEngine construction and ends after successful BeginTrans, including attach,
request publication, wait and writable-core open, excluding pipe scheduling,
process startup, update/commit and teardown. This is attach-to-acquire latency,
not the old preattached update latency.

After the outgoing facade detaches, the holder checkpoints outside measurement,
then performs 30 real indexed updates under CHECKPOINT=1000 to leave WAL above
the former 50-page close threshold. The next fresh attach wakes this sole holder.
The positive-threshold regression independently checks the logical WAL size;
measurement artifacts report physical WAL bytes without treating preallocation
as a logical-page count. Each variant/durability sample has ten unmeasured warmup
handoffs and 200 measured handoffs, with nearest-rank p50/p99, raw per-handoff
samples (including process IDs), and five serial, order-alternating before/after
pairs. DurableCommits=false/true remain separate. Cold row/index/payload/sentinel
verification follows each sample; thresholds are report-only interim evidence.
Both Python orchestration and the worker reject execution outside Linux x64
fork CI. Local Python unit tests use fake workers and never measure latency.

Dispatch `benchmark-gate.yml` on a **fork workflow ref containing the new job**,
with `shared_newcomer_only=true`, `candidate_repository=litedb-org/LiteDB`,
`candidate_ref=<fixed SHA>` and `baseline_ref=df6b16f80850d6f7ca072c2a685bf8bb2abd907a`.
An older fork workflow ref cannot accept the new input/job. This step's upstream
push alone does not install it on the fork.
