# Summary
- P5-SAFETY-CAMPAIGNS: running; exact candidate e507faad40e968aa2fcf63ae6dd01fd7def2e07b.
- Orchestrator instructed hosted extended qualification, no product changes, wait for terminal runs.
- Scratch branch v6/wip-p5-campaigns; no phase/dev pushes or PR actions.
- Failures, reachability and binaries pending.

# Steps
- Read rules, brief, development/workflow/validation/safety/data-safety guidance and Phase 3/4 reports.
- Created scratch branch directly from exact candidate; clean initial tree. Existing campaign inputs cannot select requested OS/bounded seed count.
- Inspected fuzz CLI: no extended/exhaustive switch. Will use ten seeds ×40 steps (400 aggregate vs maximum standard40), plus complete registered fault-test classes and all handle/crash/power-loss classes. Linux-only shared-wake returns zero steps on Windows/macOS and will be reported honestly.
- Created dispatch-only driver 73661c0e0; binary checkout pinned to e507faad4, ten seeds529470–529479 ×400 steps per target (100× standard40 aggregate); close-race rounds120 vs12. Static compile/diff checks pass. Initial queued run42531021244 cancelled for inventory correction: shared virtual/override names require explicit Linux/Windows entries; macOS already includes both. No result credited from superseded run.
- Final initial driver b811372fb09ea469ea884d06df22002225df4d71 pushed solely with explicit scratch refspec. Hosted run https://github.com/litedb-org/LiteDB/actions/runs/42531054661 queued:22 legs,48 production targets, three OS fault suites. Registry inventory78 hooks+22 injectors,70 referenced test files; all files exist. Every built/tested source checkout pinned e507faad4, distinct from driver SHA.
- 2026-11-08T22:38:22.987383+00:00 Hosted run42531054661 queued: {'queued:': 21, 'in_progress:': 1}; report skeleton and artifact aggregator prepared.
- 2026-11-08T22:40:14.955945+00:00 hosted 42531054661: {'queued:': 21, 'in_progress:': 1}; terminal qualification not yet claimed.
- 2026-11-08T22:42:35.152026+00:00 hosted 42531054661: {'queued:': 21, 'completed:success': 1}; terminal qualification not yet claimed.
- linux-1 success; downloaded binaries and all raw artifacts. Six targets17860 total steps, all gitSha=e507faad4 architectureX64. Boundary/integrity fixed one-step campaigns:10seeds supply10actual steps vs1standard; all other targets400steps×10 plus unchanged regression corpus. Initial reachability aggregation generated; final audit awaits remaining21legs.
- 2026-11-08T22:43:26.163387+00:00 hosted 42531054661: {'queued:': 20, 'completed:success': 1, 'in_progress:': 1}; terminal qualification not yet claimed.
- 2026-11-08T22:47:19.647827+00:00 hosted 42531054661: {'queued:': 19, 'in_progress:': 2, 'completed:success': 1}; terminal qualification not yet claimed.
- 2026-11-08T22:50:26.410285+00:00 hosted 42531054661: {'in_progress:': 3, 'queued:': 18, 'completed:success': 1}; terminal qualification not yet claimed.
- 2026-11-08T22:55:53.807315+00:00 hosted 42531054661: {'in_progress:': 3, 'completed:success': 1, 'queued:': 17, 'completed:failure': 1}; terminal qualification not yet claimed.
- linux-4 terminal failure;13target/platform groups44285steps. Three original vector seeds529476/529477/529478 reproduce in all three hosted replays, stable VECTOR_DATABASEVECTORINTEGRITYVERIFIER_L43. Classified known open product defect2952 (2980 duplicate), not flaky/harness/infrastructure; minimal independently replayed prefix100 for529477, minimizer80. Details and retained raw inputs/databases in evidence/vector-finding.md. No fixes. Driver intentionally makes replay exit1 visible even though ordinary harness treats this known finding as nonblocking.
- Round 2 resumed existing monitor (PID1761089); no campaign redispatch. Fetched PR3142 branch fix/hnsw-graph-connectivity at c2776273fe490d8e06d58a68e2d59fb85685ba51 for isolated source-archive build and focused vector replays.
- 2026-11-08T23:04:34.497750+00:00 hosted 42531054661: {'in_progress:': 4, 'completed:success': 1, 'queued:': 16, 'completed:failure': 1}; terminal qualification not yet claimed.
- 2026-11-08T23:06:55.032658+00:00 hosted 42531054661: {'in_progress:': 5, 'completed:success': 1, 'completed:failure': 1, 'queued:': 15}; terminal qualification not yet claimed.
- PR3142 c2776273 build succeeded; all three failing seeds pass400 steps, minimal seed529477 passes100/80. Byte-for-byte full original-input prefix equality established for all three. Raw replay-envelope exhaustion retained/classified; evidence vector-finding.md. #3142 fixes exercised #2952 manifestation=yes; no integration claim.
- 2026-11-08T23:07:41.783586+00:00 hosted 42531054661: {'in_progress:': 6, 'completed:success': 1, 'completed:failure': 1, 'queued:': 14}; terminal qualification not yet claimed.
- 2026-11-08T23:10:49.021132+00:00 hosted 42531054661: {'in_progress:': 7, 'completed:success': 1, 'completed:failure': 1, 'queued:': 13}; terminal qualification not yet claimed.
- 2026-11-08T23:17:38.223699+00:00 hosted 42531054661: {'in_progress:': 8, 'completed:success': 2, 'completed:failure': 1, 'queued:': 11}; terminal qualification not yet claimed.
