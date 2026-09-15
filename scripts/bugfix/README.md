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
| `snapshot` | Baseline arguments, `--test-inventory`, `--allowed-failure-classes`, and `--failure-normalization` | Records the independently discovered baseline inventory, admitting only explicitly classified failures and skips |
| `compare` | Candidate arguments, `--test-inventory`, `--failure-normalization`, and `--ledger` | Discovery matches the baseline ledger; targets pass; no new failures/skips or changed known failures |

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
definition ID, every result instance, and every failure text, not just display
names or totals. Repeated display names retain their multiplicity; VSTest
execution IDs are checked for completeness but excluded from stable comparison
because the runner regenerates them. Keep the ledger immutable with its baseline
run evidence. Baseline/candidate builds must use the same framework, test hooks,
dependencies, and controller revision.

Unexpected passes block `compare` and are listed individually for investigation;
the gate has no blanket override switch. Review related fixes and update the
contract through the controller before accepting them. New test cases must pass.
Previously skipped cases remain visible and must retain their outcome.
Reviewed intermittent classes are carried into the immutable ledger. A
Passed-to-Failed or Failed-to-Passed flip in one of those classes remains
non-accepted and is reported in the structured `inconclusive_changes` array;
other outcome changes remain errors.

Failure comparison preserves assertion/exception text before stack frames,
including complete quoted stack traces when the stack itself is the assertion
subject. Diagnostic frames following the assertion are excluded. `--failure-normalization`
supplies reviewed regexes scoped to exact test names. Every rule has an exact
expected match count or an explicit set of runtime-specific counts. The baseline
must match every rule, and its policy digest is bound into ledger provenance. A
candidate with a different diagnostic shape retains its original failure and is
rejected. Harness errors, zero selections,
duplicate result identities, incomplete results, inconsistent counters, and
unrelated runner errors fail closed.

An unchanged failing test whose normalized assertion changes is reported in
`classification_changes` with both failure hashes. It remains rejected and
routes to investigation without consuming another code repair attempt.

Run bounded gate verification with:

```text
python -m unittest discover -s scripts/bugfix -p "test_*.py"
```
