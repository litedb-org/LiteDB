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
