# Versioned #2825 CI harness correction

The paired pilot capture exposed a failure-classifier gap in the historical
LiteDB 5.0.20 variant of `Issue_2825_FreeListRace`. The historical process
reproduced the expected empty-page defect, but one concurrent worker also threw
`page must be writable to support changes` through the data-page insertion
path. The classifier recognized the same message through validation and index
insertion paths only. Its catch filter therefore rejected the aggregate, and
the process returned exit code 20 before completing all three attempts and
emitting its explicit `BUG_2825_CONFIRMED` marker.

The current-library candidate variant completed all three attempts and every
persistence assertion. That result does not make the failed historical artifact
acceptable. The original candidate artifact `10404806577` and job
`104440143579` remain a `harness_error`; the corresponding baseline artifact is
`10403704143`.

## Reviewed classifier change

The final-promotion full matrix applies a separately versioned overlay to the
three #2825 repro jobs. It adds one secondary failure shape to the existing
classifier. An aggregate is reported only when it still contains a primary
`INVALID_DATAFILE_STATE` empty-page error from one of the already approved
origins and every other inner exception is approved. The new secondary must
have the exact error code and message and the following contiguous stack-frame
sequence:

1. `BasePage.InternalInsert`
2. `BasePage.Insert`
3. `DataPage.InsertBlock`
4. the `DataService.Insert` source iterator
5. `BufferWriter.MoveForward`
6. `BufferWriter.Write`
7. `BufferWriter.WriteString`
8. `BufferWriter.WriteElement`
9. `BufferWriter.WriteDocument`
10. `DataService.Insert`
11. `LiteEngine.InsertDocument`

The matcher accepts LF and CRLF stack text and source-location suffixes. It
rejects missing, reordered, renamed, or separated frames. Tests also reject a
secondary without the primary, a primary with an unrelated origin, a different
exception type, error code, or message, and unrelated text or frames inserted
between every approved neighbor.

## Frozen source and provenance

The frozen `Issue2825_Tests.cs` fixture, its three-attempt runner, repro metadata,
and original classifier remain unchanged in the source commits under test. The
fixture still runs four workers, 30 rounds, and 12 rows with all original
reopen, payload, ID, and insertion assertions.

- `.github/bugfix/issue-2825-harness-overlay.json` pins the original and
  effective classifier blobs, the runner, repro metadata, and frozen test blob.
- `.github/bugfix/issue-2825-classifier-overlay.cs` contains the reviewed
  effective classifier.
- `.github/scripts/apply_issue_2825_harness_overlay.py` mechanically derives the
  effective blob from the original unique branch, verifies every pinned
  dependency, applies only that classifier file, and emits job provenance.
- `.github/bugfix/Issue2825HarnessOverlay.Tests.csproj` and its adjacent test
  source compile the effective overlay directly without changing the frozen
  source tree.

Each #2825 OS artifact records the source commit, immutable capture definition,
manifest digest, original and effective blob identities, dependency hashes, and
classifier/workload contract. The collector and comparator require the same
manifest for baseline and candidate, require exact attestations on all three
#2825 jobs, and reject an overlay record on any other job.

This corrects CI classification for a specific observed historical secondary
failure. It does not classify #2825 as fixed, relabel prior failed evidence, add
a quarantine, or select a passing retry. Per-fix integration uses one combined candidate CI profile and three
independent reviews; platform and compatibility checks are selected by that profile. The full 109-job comparison,
including this overlay, runs once for final promotion after the sweep.
