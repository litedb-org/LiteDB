## Summary
- State: investigating; classification pending.
- Candidate e507faad4 contains b70b79ec2 (merge-base exit 0).
- Comparison head ae2eae2af0e485b892d2467699c9bb5dcfb37d60.
- Scratch branch v6/wip-f-p5-macos-cut; no product edits.
- Orchestrator instructed completed hosted macOS runs, overriding brief ci-pending default.

## Steps
- Read rules and brief, created log in first minute, inspected retained seed 529470 replay (400 rounds, empty input SHA256).
- Fetched Phase5, created scratch branch; existing F-P3 checkpoint scope present. Preparing bounded exact-revision hosted comparison.
2026-11-09T05:40:39Z Scratch hosted comparison pushed and dispatched; direct no-reader/held-reader controls plus unchanged replay count400, two exact product revisions.
- WIP cbd381e16 pushed early; harness b36c631c8 pushed. Initial queued run42568672301 cancelled to retain a separate unmodified exact-revision replay binary; diagnostics affect only a second harness build. No product edits. Replay SHA256 2662849d24ee054738ad42b575a505272a8e6f9a89f192639202a7a5e3d62a36; input empty hash verified. Python compile checks pass. Both source revisions have identical SharedProcessFuzzer and scope files.
- Exact scratch head1625912ef1f063f11e04175ae589c46608dcc6d4 dispatched https://github.com/litedb-org/LiteDB/actions/runs/42568832502 (macos-15, runtime net8, matrix candidate/current). Replay is all400 original rounds, hard3h bound per revision; workflow200min bound. Direct probe holds an admitted precommit read cursor or no reader, validates marker identity and twice-reopened committed rows. Separate raw/diagnostic builds; unmodified binaries and SHA256 retained. Queue pending, not qualification evidence.
- Source audit found small read results can buffer and release the lease; strengthened probe control row with128KiB payload and explicit MappedReaderTable.LiveVersions nonempty assertion. Queued1625912ef run42568832502 cancelled/replaced before evidence collection. Both cancelled runs are setup supersessions, never accepted results. Source scope remains scratch harness/workflow only.
2026-11-09T05:51:19Z Local net8 diagnostic harness build passed; fixed namespace serializer collision before hosted execution; replaced queued42569331886. No local fuzz/suite run.
2026-11-09T05:55:49Z Hosted42569656324 both exact-revision macOS legs queued; no completed evidence yet.
2026-11-09T05:59:13Z Both macOS legs still queued; static audit notes conservative checkpoint early-return on unknown registry plus active-reader floors; classification not inferred from source alone.
2026-11-09T06:07:17Z Both jobs remain queued; inspecting check-run scheduling output.
2026-11-09T06:11:44Z Hosted comparison still queued, no check-run errors or runner assignment.
2026-11-09T06:16:50Z Hosted42569656324 queued ~25min both legs, no runner. Concurrent newer Phase5 workflows also queued.
- Prepared additive forced closing-participant probe: child commits then waits at scratch-only harness gate; peer dispose pauses at existing FinalCloseStage slot-cleared while liveness lock remains held; child checkpoints. This directly tests SharedMemoryReaderTable.Scan's live-but-not-ready null result rather than inferring a reader race. Local net8 diagnostic build passed0errors. Original full replay run remains intact. Phase5 advanced tobd79b12b6 during queue; no changes to SharedProcessFuzzer/scope since pinnedae2eae2af.
2026-11-09T06:21:13Z Original full replay42569656324 and additive direct-only42572211702 queued, no runner assignment.
2026-11-09T06:24:24Z Queue persists; unrelated previously active Phase4 run has no unfinished jobs. No unrelated workflows altered.
- Additional direct-only run https://github.com/litedb-org/LiteDB/actions/runs/42572211702 exact scratch690e577af; original full replay https://github.com/litedb-org/LiteDB/actions/runs/42569656324 exact scratch03d553038. Both compare exact producte507faad40e968aa2fcf63ae6dd01fd7def2e07b/ae2eae2af0e485b892d2467699c9bb5dcfb37d60, queued. Tentative harness proposal pending hosted evidence: SharedProcessFuzzer.cs228-229 RequiresExclusiveCheckpointStart should admit all checkpoint cuts21..26 before starting peers (existing callsite66-67), covering live-but-not-ready attach/close and live snapshot deferrals; retain concurrency for other cuts. No fix implemented; no product classification inferred.
