# Deterministic bugfix gates

Run these scripts from the trusted controller checkout. Candidate code lives in
a separate checkout. The caller records the actual OS, process architecture,
framework, test process exit code, workflow/run identity, and checkout commits.
These scripts validate supplied evidence; they cannot authenticate an artifact's
GitHub origin. The controller must authenticate that before using it.

`issues.json` adds execution contracts keyed by the existing open-bug inventory
issue numbers. The initial canary is #2874. Its six parameterized regressions and
one valid-input control are named exactly, including xUnit's `···` array display.
Original test blobs are frozen to `dd937719f7eee53c512f50ac604cab639bf42a4c`.

## Commands

All commands require `--issue`, `--base-sha`, and `--output`. SHAs must be full
40-character lowercase commit IDs. `--manifest` defaults to the adjacent JSON.
Every command writes a structured report and exits zero only on acceptance.

| Command | Additional arguments | Acceptance |
| --- | --- | --- |
| `verify-tests` | `--repository` | Baseline contains exact original test blobs |
| `protect` | `--repository --candidate-sha` | Candidate descends from baseline, preserves original tests, and changes only allowlisted production files or newly added C# tests |
| `baseline` | Common evidence arguments below | Exact defect assertions fail and controls pass |
| `focused` | Baseline and candidate evidence arguments | Same exact selection goes from established defect to all passing |
| `snapshot` | Baseline arguments, `--test-inventory`, and `--allowed-failure-classes` | Records the independently discovered baseline inventory, admitting only explicitly classified failures and skips |
| `compare` | Candidate arguments, `--test-inventory`, and `--ledger` | Discovery matches the baseline ledger; targets pass; no new failures/skips or changed known failures |

Common evidence arguments are `--environment` (for example
`linux-x64-net8.0`) and `--test-definition-sha` (trusted controller revision).
Baseline arguments are `--baseline-trx --baseline-exit-code`. Candidate
arguments are `--candidate-trx --candidate-exit-code --candidate-sha`.

For example:

```text
python scripts/bugfix/gate.py focused --issue 2874 --base-sha <base> --candidate-sha <candidate> --test-definition-sha <controller> --environment linux-x64-net8.0 --baseline-trx baseline.trx --baseline-exit-code 1 --candidate-trx candidate.trx --candidate-exit-code 0 --output focused.json
```

`snapshot`'s allowlist is reviewed controller policy. Failure classes classify
known assertion failures; skipped test identities must be listed individually:

```json
{
  "schema_version": 1,
  "classes": ["LiteDB.Tests.Issues.Issue2874_Tests"],
  "skipped_tests": ["LiteDB.Tests.PlatformTests.RequiresWindows"]
}
```

Extend it with explicitly identified pending issue and audit test classes before
snapshotting a complete suite. `--test-inventory` is a JSON file with
`schema_version: 1` and a nonempty `tests` array produced by a separate full test
discovery. Both baseline and candidate execution must exactly match their
independently generated inventory. The generated ledger stores every exact test
case and failure text, not just class names or totals. Keep that ledger immutable
with its baseline run evidence. Baseline/candidate builds must use the same
framework, test hooks, dependencies, and controller revision.

Unexpected passes block `compare` and are listed individually for investigation;
the gate has no blanket override switch. Review related fixes and update the
contract through the controller before accepting them. New test cases must pass.
Previously skipped cases remain visible and must retain their outcome.

Failure comparison preserves assertion/exception text before stack frames,
discarding only stack locations that change across checkouts. Nondeterministic
assertion messages therefore require explicit investigation rather than a loose
automatic match. Harness errors, zero selections, duplicate names, incomplete
results, inconsistent counters, and unrelated runner errors fail closed.

Run bounded gate verification with:

```text
python -m unittest discover -s scripts/bugfix -p "test_*.py"
```
