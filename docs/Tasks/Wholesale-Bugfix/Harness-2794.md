# Versioned #2794 CI harness correction

The historical Windows/.NET Framework 4.8 handoff control failed twice because
its worker used one 60-second stopwatch for the entire process. A later wait
therefore inherited time spent completing earlier rounds. The current-library
variant passed, and an isolated historical retry also passed. Those observations
do not establish a product regression or justify discarding the failed runs.

The campaign applies a separately versioned timing correction to this control:
each `WaitUntil` starts its own 60-second stopwatch. The existing 15-second
readiness barrier and concurrent 90-second client/service completion waits remain
unchanged, as do the seed/verification worker limits and outer runner timeout.
All 24 jobs, 24 rounds, three attempts, data assertions, updates, checkpoints,
fresh read-only verification, and file-hash checks remain intact.

## Source and evidence

Production and frozen regression source still come from the requested source
commit. Only the #2794 process-control worker receives the explicit CI overlay;
the candidate commit and all `LiteDB.Tests` source remain unchanged.

- `.github/bugfix/issue-2794-harness-overlay.json` pins the original and effective
  worker blobs, parent runner, ledger assertions, and repro metadata.
- `.github/bugfix/issue-2794-worker-overlay.cs` is the reviewed effective worker.
- `.github/scripts/apply_issue_2794_harness_overlay.py` derives the exact timing
  edits from the original blob and rejects any additional change. It also checks
  that only the intended tracked file changed.
- Every #2794 OS artifact records its source commit, capture definition, manifest,
  original/effective worker hashes, dependency hashes, and timeout/workload
  contract. The collector and comparator require matching trusted provenance on
  all three jobs and reject overlay records elsewhere.

Both baseline and candidate need fresh full runs using the same immutable capture
definition and overlay manifest. Earlier failures remain diagnosis evidence.
This is a harness correction, not an accepted fix for the unconfirmed historical
#2794 report, and it introduces no additional quarantine. The three compile-blocked
#2854 jobs remain the only excluded jobs.

The full-CI dispatch requires `harness_overlay_manifest_sha256` in addition to
`issue`, `source_sha`, `evidence_definition_sha`, and `quarantine_sha256`. Use the
manifest's committed LF bytes when computing its digest. The integration tool
retains the manifest, effective source, verification script, and their hashes
alongside the actual run artifacts.
