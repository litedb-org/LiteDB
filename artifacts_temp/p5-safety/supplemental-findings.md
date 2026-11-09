# Additional classified findings

## api-boundary: harness scaling defect, not storage corruption

Candidate e507faad40e968aa2fcf63ae6dd01fd7def2e07b. Hosted linux-0: all ten seeds529470–529479 fail at348; all ten independent minimized hosted replays reproduce at348. Identity `LiteDB.LiteException`; message `Invalid collection name 'under_score_348': There is no space in header this collection name`. No seed randomness is consumed (empty input/trace hashes).

Evidence: `artifacts/p5-linux-0/evidence/api-boundary-529470.log` through `api-boundary-529479.log`, `replay-*.log`, `commands.json`, `replay-index.json` and corresponding `run.json` files. Hosted job linked in terminal report; raw logs retained. Replay invocation is `dotnet LiteDB.Fuzz.dll --replay <original>/minimized-replay.json --artifact-dir <out> --max-artifact-mb 0`; all exit1,348 steps. No extra local long campaign was run.

Source cause: `LiteDB.Fuzz/Targets/ApiBoundaryFuzzer.cs:69` retains a new `under_score_<step>` collection on every iteration. `HeaderPage.COLLECTIONS_SIZE` is8000 bytes; `CollectionService.CheckName` explicitly rejects a name that exceeds `header.GetAvailableCollectionSpace()` before allocation. Raising the external budget from standard40 to400 exposes the harness's unbounded collection accumulation. This is a harness scaling defect: the target assumes every new legal name still fits the finite header and fails to handle its capacity limit. It is not evidence of corrupted previously committed data. Ten deterministic same-identity hosted replays establish reproducibility, not flakiness. No same-job green dev comparison exists for this newly introduced dispatch-only campaign; no flaky/infrastructure or historical-green claim is made.

Disposition: no product/harness edits in this task. Preserve failure and record api-boundary's requested400-step budget as truncated348; later phases in each seed did not run. The target needs bounded collection reuse/cleanup or an explicit independent capacity/refusal oracle in a separate scoped harness change. Candidate qualification cannot count the unexecuted52 steps per seed as passed.
