# Additional classified findings

## api-boundary: harness scaling defect, not storage corruption

Candidate e507faad40e968aa2fcf63ae6dd01fd7def2e07b. Hosted linux-0: all ten seeds529470–529479 fail at348; all ten independent minimized hosted replays reproduce at348. Identity `LiteDB.LiteException`; message `Invalid collection name 'under_score_348': There is no space in header this collection name`. No seed randomness is consumed (empty input/trace hashes).

Evidence: `artifacts/p5-linux-0/evidence/api-boundary-529470.log` through `api-boundary-529479.log`, `replay-*.log`, `commands.json`, `replay-index.json` and corresponding `run.json` files. Hosted job linked in terminal report; raw logs retained. Replay invocation is `dotnet LiteDB.Fuzz.dll --replay <original>/minimized-replay.json --artifact-dir <out> --max-artifact-mb 0`; all exit1,348 steps. No extra local long campaign was run.

Source cause: `LiteDB.Fuzz/Targets/ApiBoundaryFuzzer.cs:69` retains a new `under_score_<step>` collection on every iteration. `HeaderPage.COLLECTIONS_SIZE` is8000 bytes; `CollectionService.CheckName` explicitly rejects a name that exceeds `header.GetAvailableCollectionSpace()` before allocation. Raising the external budget from standard40 to400 exposes the harness's unbounded collection accumulation. This is a harness scaling defect: the target assumes every new legal name still fits the finite header and fails to handle its capacity limit. It is not evidence of corrupted previously committed data. Ten deterministic same-identity hosted replays establish reproducibility, not flakiness. No same-job green dev comparison exists for this newly introduced dispatch-only campaign; no flaky/infrastructure or historical-green claim is made.

Disposition: no product/harness edits in this task. Preserve failure and record api-boundary's requested400-step budget as truncated348; later phases in each seed did not run. The target needs bounded collection reuse/cleanup or an explicit independent capacity/refusal oracle in a separate scoped harness change. Candidate qualification cannot count the unexecuted52 steps per seed as passed.

## snapshot on Windows: harness IPC sharing race, plus incomplete recorded input

Hosted windows-2 originals: all ten seeds fail; nine with `SNAPSHOT_READER_FAILED` wrapping IOException on `reader-*.read`, one529472 with IOException on `reader-*.result`. These are harness control files, not database/WAL files. Source `SnapshotFuzzer.RunChild` line98 deletes `.read` immediately after File.Exists while parent `ReaderProcess.Validate` line203 is still executing non-atomic File.WriteAllText. Windows disallows deleting that open handle. The `.result` read at line205 can also encounter a sharing conflict. Failures occur in handshake I/O before an oracle comparison, not a reported snapshot data mismatch.

All ten independent hosted replay commands are retained in `artifacts/p5-windows-2/evidence/replay-index.json` and `commands.json`; raw logs `snapshot-52947*.log` and `replay-*.log`, input artifacts and run.json files are retained. Replay disposition:

| Seed | Original step / failure | Replay step / result |
| --- | --- | --- |
|529470|41 / .read sharing|30 / .read sharing|
|529471|18 / .read sharing|10 / .read sharing|
|529472|47 / .result sharing|9 / .read sharing|
|529473|41 / .read sharing|41 / .read sharing|
|529474|11 / .read sharing|11 / recorded-input exhaustion|
|529475|97 / .read sharing|55 / .read sharing|
|529476|48 / .read sharing|48 / recorded-input exhaustion|
|529477|11 / .read sharing|12 / recorded-input exhaustion|
|529478|129 / .read sharing|20 / .read sharing|
|529479|65 / .read sharing|65 / passes shortened replay|

Classification: harness IPC sharing race, not an infrastructure flake or proven product snapshot regression. Six independent replays reproduce the sharing failure family (529473 at the identical step); three instead exhaust the input retained at the interrupted original run, and one passes. **Exact-seed deterministic reproduction is not established for every seed**; retain this as a replay/qualification limitation rather than claiming all ten were reproduced identically. No Windows runtime is available locally and no second campaign was dispatched. Source/lifetime analysis classifies the observed exceptions, but it cannot qualify the unexecuted snapshot schedules or exclude hidden product bugs behind the aborted harness. A separate scoped harness change should atomically publish the read request and handle control-file sharing; no fix was made here. There is no same-job green dev baseline for this new campaign, and no expected/flaky classification is asserted.

### Windows api-boundary confirmation

windows-0 terminal failure repeats all ten original seeds and all ten independent hosted replays at348 steps, with identical `under_score_348` header capacity rejection. Raw originals/replays and command index are under `artifacts/p5-windows-0/evidence/`. Classification matches Linux: harness scaling defect, no product-data mismatch. Across Linux/Windows this class has20 original failures+20 independent replay failures.

## macOS Shared: unproven crash boundary and campaign timeout (unresolved origin)

macos-shared-handle ended cancelled at the350-minute job budget (started23:10:06, cancellation05:00:19). Downloaded partial artifact `p5-macos-shared-handle`; exact job log `macos-shared-job.log` from GitHub jobs/128885108784/logs, lines around the cancellation. No second campaign dispatched.

Original shared seed529470 fails at282 steps with `SHARED_SHAREDPROCESSFUZZER_L82`: the crash child exits134 without proving the selected hook fired. Retained trace.jsonl pins round282/worker0/crashAt22 = `checkpoint-after-page-write`; child exits with `Configured shared-process crash boundary was not reached` at SharedProcessFuzzer.cs179, and the parent refuses to credit the cut at81–82. No data-integrity mismatch is reported. Source SharedCheckpointCrashScope tries to hold the mutex through commit/checkpoint, but this evidence does not explain why the cut was missed. Classification: **fault-reachability/qualification failure, origin unresolved**, not proven product corruption, not a presumed flake or confirmed harness-only defect. No deterministic replay completed for this failure; that is an explicit blocker.

Raw evidence: `artifacts/p5-macos-shared-handle/evidence/shared-529470.log`, `fuzz/shared/529470/*/{run.json,trace.jsonl,processes.jsonl,shared.db,state-shared.db,worker-*.jsonl,replay.json}`. The failure run took6274seconds; commands.json records exit1. Seed529471's400step invocation passed after8578seconds. Seed529472 was running when the job budget cancelled the driver; no end-of-driver replay-index or binary-sha256 file exists. shared-legacy/shared-wake/transaction-handle in this macOS leg were not reached. Partial records and binaries are retained and will be hashed independently; absent final manifests are not represented as completed evidence.

Disposition: candidate qualification remains blocked. A fresh bounded macOS reproduction with forced checkpoint-hook admission is needed outside this no-redispatch round. Linux/Windows Shared results cannot substitute for this missing macOS crash proof.

## linux-2: job-budget cancellation and failed evidence upload

Final leg cancelled at350 minutes (23:37:13–05:27:25). Job log `linux-2-job.log` proves48 completed exit0 invocations: ten each bson/compact-power-loss/linq-cache/power-loss and eight snapshot seeds529470–529477. Seed529478 snapshot was active;529479 and transaction-handle were not completed. Logs retain commands/config, not the run.json step metrics, traces or binary hashes; do not equate exit0 with retained clean oracle evidence or impute400 completed steps per invocation.

The always-upload step started with1286 files, then failed creating its ZIP: ENOENT on live `reader-267-third-primary.ready` beneath snapshot529478. The runner killed orphan child processes only after the upload attempt. Classification: **campaign budget exhaustion plus CI evidence-collection defect** (upload raced still-mutating harness files), not a demonstrated product/data mismatch. Exact repeat of the350-minute campaign was deliberately not dispatched. Source/process ordering and concrete upload log establish the evidence loss; no flaky claim is made.

GitHub exposes21 archives for22jobs; **no linux-2 archive exists to download**. Entire terminal job log and parsed `linux-2-log-commands.json` are retained. The lost binaries cannot be SHA256-verified, affected reachability cannot be counted, and the missing raw step metrics are excluded from the365110 reported-step lower bound. This blocks complete qualification and the requested all-leg binary/evidence inventory in this round. A separate bounded rerun with orderly child shutdown and stable artifact staging would be needed; no product changes or redispatch performed here.
