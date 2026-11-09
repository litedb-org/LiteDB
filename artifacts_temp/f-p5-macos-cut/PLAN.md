## Summary
- State: done (research classification only). Classification (a): checkpoint crash-cut harness reachability bug, still reproducible on current head under forced schedules.
- Final hosted run https://github.com/litedb-org/LiteDB/actions/runs/42579384408 completed SUCCESS on scratch3c9a0d39111450356cb00e55955135b453f384db; both pinned macOS/net8 legs executed.
- Candidate e507faad40e968aa2fcf63ae6dd01fd7def2e07b and current bd79b12b61f89c5af0e197cfc70273f307b537b5 each passed unchanged400-round replay,26cuts,1200processes,400integrity checks.
- Both revisions' direct controls: no-reader hits22|checkpoint-after-page-write; held-reader and forced closing participant reproduce exact missing-boundary error; all preserve5committed rows over2cold reopens.
- Proposed fix: LiteDB.Fuzz/Targets/SharedProcessFuzzer.cs:228-229 extend existing admission predicate to position >=21 && position <=26; keep separate concurrent reader/attach/close deferral guards. Proposal only, not implemented or qualified.
- Initial run42575162758 was comparison setup failure(d): injected assignment from void Checkpoint API. Fixed only scratch research/f-p5-macos-cut/prepare.py:10; both local and hosted builds passed.
- Exactly one round2 redispatch; no product edits, PRs, watches, unrelated cancellations, local fuzz or full tests. Evidence in f-p5-macos-cut-evidence/round2-*.
- Blocker: none for classification. Harness fix and qualification remain follow-up; no claim that current head fixes the finding or proves general safety.

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
2026-11-09T06:33:19Z Both comparison workflows remain queued; continuing required wait, no qualification claim.
2026-11-09T06:38:06Z Hosted macOS comparison still queued, ~45min original. All requested product sources untouched; pending hosted evidence blocks classification.
2026-11-09T06:43:03Z Both runs queued, zero assigned macOS runners; required wait continues.
-06:45UTC first candidate job129003849277 executed and FAILED CHECKOUT ONLY: actions/checkout interprets abbreviatede507faad4 as branch/tag, fetch retries exit1. No build/tests executed, no product/harness behavior classification. Raw terminal log retained in f-p5-macos-cut-evidence/first-candidate-job.log. Correcting matrix to full40charSHA; cancelling remaining queued legs and consolidating all3probes+original replay into corrected run. No flake claim.
2026-11-09T06:48:31Z Corrected full-SHA comparison scratch089004b30; current Phase5 snapshot refreshed bd79b12b61f89c5af0e197cfc70273f307b537b5; earlier ae2e comparison cancelled before any product execution.
2026-11-09T06:52:29Z Corrected run42575162758 both full-SHA legs queued; waiting for actual macOS execution.
2026-11-09T06:59:31Z Corrected full-SHA comparison remains queued; no new errors/results.
2026-11-09T07:06:00Z Required full-SHA run42575162758 remains queued on both macOS legs.
2026-11-09T07:10:03Z Corrected comparison still queued; no macOS test execution or result.
2026-11-09T07:16:07Z Corrected comparison still queued; classification remains unproven.

2026-11-09T07:25Z Round2 read rules/brief/prior log, downloaded gh --log-failed + artifacts. Both legs fail injected var cutCheckpointPages = db.Checkpoint(); (void); replay step skipped. Orchestrator instructed one corrected dispatch then wait, overriding brief no-wait default.

2026-11-09T07:27Z Corrected scratch prepare.py records completion of void Checkpoint API instead of assigning a value. Exact-revision diagnostic net8 TestingEnabled builds both passed (0 errors); commands/results retained in round2-build-* and verify-round2.py. No local fuzz or broad test run.

2026-11-09T07:27:01Z Round2 sole redispatch https://github.com/litedb-org/LiteDB/actions/runs/42579384408 exact scratch3c9a0d39111450356cb00e55955135b453f384db; both exact product revisions retained, direct_only=false, queued. No more dispatches permitted. Wait until completed; no-runner deadline about09:27UTC.

2026-11-09T07:32:21Z Run42579384408 both macOS legs running actual direct/replay step after successful unmodified and diagnostic builds. Hosted runners obtained; no-runner blocker no longer applies. Next CI poll >=07:37UTC.

2026-11-09T07:38:50Z Both comparison jobs still in_progress; result unavailable, no further dispatch or cancellations.

2026-11-09T07:46:07Z Both jobs remain in_progress (129034259293 /129034259588). Original replay400 rounds retained unchanged, waiting for completion; no new hosted run.

2026-11-09T07:52:19Z Both macOS legs remain in_progress; setup builds passed, awaiting completed direct/replay artifacts. No qualification claim.

2026-11-09T08:04:08Z Both macOS legs still in_progress. Probe provenance: step282 maps (282-1)%26+1=22/checkpoint-after-page-write, worker0 seed529470+282*397=641424 (same direct probe); original round has2workers. No claim of deterministic schedule replay: native process interleaving can move the failure round.

2026-11-09T08:10:18Z Both jobs still in_progress in sole redispatch42579384408 (scratch3c9a0d391); wait continues. Recording resumable state on scratch branch, documentation-only successor does not change run source SHA or dispatch more CI.

2026-11-09T08:22:39Z Both jobs still in_progress; no additional run or cancellation, awaiting original400-round replay completion within3h subprocess/200min job bounds. Scratch log successor d341a331a pushed; executed run remains3c9a0d391.

2026-11-09T08:40:01Z Both comparison jobs continue in_progress, ~73min after dispatch; no additional action on CI. Poll interval5–7min maintained. Classification of original finding pending artifacts.

2026-11-09T08:57:21Z Both hosted comparison jobs remain in_progress ~90min. Sole redispatch retained; no more CI started. No product safety or fixed-current-head claim from runtime duration.

2026-11-09T09:19:40Z Both comparison jobs still running; no-hosted-runners cutoff does not apply because both started and built successfully around07:27. Waiting for completed evidence (replay10800s budget, workflow200min).

2026-11-09T09:44Z Current bd79b12b6 completed success; artifact downloaded round2-final/current. Direct exit0 (2.124s): no-reader marker22|checkpoint-after-page-write/exit134; held-reader and forced closing-participant marker null, exact Configured shared-process crash boundary was not reached/exit134, Checkpoint returned. All3 scenarios assert5 committed rows across2cold reopens. Unmodified replay exit0,400steps/400rounds/1200processes/26cuts/14internal/400integrityChecks,8029.184s; this is NOT evidence fixed-on-current because forced controls reproduce harness failure at current. Candidate still running. run.json gitSha reflects invoking scratch CWD, not binary source; authoritative product-sha.txt plus original-sha256.txt and retained binaries identify exact product revision built before prepare.py. No product edits.

## Round 2 final result (2026-11-09T09:50Z)
Final run42579384408 completed SUCCESS, both jobs; downloaded both artifacts and full hosted log. All retained original-binary DLL SHA256 match original-sha256.txt; product-sha.txt identifies exact candidate/current source. Important metadata limit: replay run.json gitSha/dirty reflect the invoking scratch checkout after diagnostic overlay, not original binary provenance. Build-before-overlay workflow and retained hashes establish provenance instead.

| Revision | Direct exit/time | Unmodified replay exit/time | Rounds / processes / cuts / integrity checks |
| --- | --- | --- | --- |
| e507faad40e968aa2fcf63ae6dd01fd7def2e07b | 0 /2.416s | 0 /8124.162s |400 /1200 /26 /400 |
| bd79b12b61f89c5af0e197cfc70273f307b537b5 | 0 /2.124s | 0 /8029.184s |400 /1200 /26 /400 |

Both direct/probe.json files report no-reader exit134 with exact22|checkpoint-after-page-write marker; held-reader and closing-participant exit134 with no marker and exact Configured shared-process crash boundary was not reached. Completion sidecars show Checkpoint returned in both deferred cases. Direct probe holds a proven mapped lease for the reader control, and forces FinalCloseStage slot-cleared while participant liveness is still held for the closing control. Each mode validates control+4acknowledged writes in2cold reopens. This is process-death/OS-cache evidence, not modeled power loss or device testing.

Both unchanged replays completed400steps,47,377acknowledged rows,26cuts/14internal cuts,400integrity checks. At step282, each trace shows crashAt22 and actual checkpoint-after-page-write FailFast. Thus the original native interleaving did not recur in these full campaigns; do not claim deterministic historical schedule replay or fails-before/passes-after proof. Forced controls separately demonstrate the exact failure identity at both revisions, including current. The source files SharedProcessFuzzer and SharedCheckpointCrashScope are identical between those revisions.

Cause: SharedCheckpointCrashScope.cs retains WRITE ownership, but does not make every checkpoint transition reachable. SharedMemoryReaderTable.cs:324-326 returns unknown/null for a live participant whose mapped slot is not ready (attach/close). WalIndexService.Checkpoint.cs:142-146 safely declines all work on unknown registry. A pinned older snapshot can also leave no eligible pages (lines155-156,183-193). DiskService.cs:338-355 can hit the page-write marker only for an actual eligible page. SharedProcessFuzzer.cs:176,179 assumes an unconditional checkpoint hook and throws; parent line82 correctly refuses to treat an unmarked exception as a crash-cut hit. No product data-loss finding established.

Exact proposed harness fix (not applied): LiteDB.Fuzz/Targets/SharedProcessFuzzer.cs:228-229 change RequiresExclusiveCheckpointStart to `position >= 21 && position <= 26` (all6checkpoint boundaries), using existing wait callsite66-67. This limits the mandatory-marker campaign's crash worker admission before peers can hold snapshots or expose transitional slots; it does not change product checkpoint refusal semantics. Other20crash positions retain their concurrent schedule. Because this changes concurrency coverage, implementation must also retain/add dedicated concurrent attach/close/pinned-reader guards proving safe deferral, eventual checkpoint progress, real overlap and committed-row recovery; do not substitute sequential crash-cut evidence for those guards. Probe code and forced schedules here provide a concrete starting point. Keep marker and26boundary coverage assertions strict. Qualification of that proposed source fix is outside this no-product-changes research step and has not been demonstrated.

Setup correction was independently verified: failed run42575162758 both legs had CS0815 at injected SharedProcessFuzzer.cs:188; original binary builds succeeded, all execution skipped. research/f-p5-macos-cut/prepare.py:10 now invokes void db.Checkpoint() and records returned text, instead of assigning its nonexistent result. Local verification script archives both exact revisions and runs prepare.py plus `dotnet build source/LiteDB.Fuzz -c Release -f net8.0 -p:TestingEnabled=true -p:GitVersionEnabled=false` (both0errors); same diagnostic builds succeeded hosted. No second redispatch, no product changes.

Evidence paths: round2-failed.log, round2-run.json, round2-artifacts/ (initial setup failure); round2-final-complete.log, round2-retry-status.json, round2-final/{candidate,current}/{commands.json,product-sha.txt,original-sha256.txt,original-binary/,direct/probe.json,direct/*/stderr.log,replay.log,replay/*/run.json,replay/*/trace.jsonl}. Commands were gh run view --log-failed /gh run download for initial run, gh workflow run fuzz.yml --ref v6/wip-f-p5-macos-cut -f direct_only=false once, gh run view --json status,conclusion,jobs at5–7min intervals, and final gh run download +gh run view --log. No green-dev comparison/flake assertion is made; forced exact-revision controls determine classification instead.
