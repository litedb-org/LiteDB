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
