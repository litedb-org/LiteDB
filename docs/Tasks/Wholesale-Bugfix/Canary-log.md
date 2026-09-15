# Canary rollout log

## Scope

The first issue is #2874, ObjectId byte-array argument validation. Its six
regressions and one valid-input control remain frozen at
`dd937719f7eee53c512f50ac604cab639bf42a4c`. No production fix has been accepted
or integrated at this point.

## Verified behavior

- The configured Responses endpoint completed authenticated streaming requests
  with both [Astra](https://github.com/litedb-org/LiteDB/actions/runs/34971658932)
  and [Sol](https://github.com/litedb-org/LiteDB/actions/runs/34973941138).
- An [actual gh-aw Astra canary](https://github.com/litedb-org/LiteDB/actions/runs/34973211222)
  read the frozen regression source and returned a checked result.
- [Controller-dispatched baseline CI](https://github.com/litedb-org/LiteDB/actions/runs/34975640587)
  confirmed all six expected failures and the passing control. The check job ran
  for 42 seconds; the whole workflow took 52 seconds including scheduling.
- An [initial fix attempt](https://github.com/litedb-org/LiteDB/actions/runs/34974068624)
  reported passing local regressions, but its result used the wrong JSON shape.
  The collector rejected it. It did not become an accepted candidate.
- [Controller canary v1](https://github.com/litedb-org/LiteDB/actions/runs/34975617067)
  rejected successful-looking workflows whose agent job had been skipped.
  Its state is explicitly blocked; it cannot resume as accepted evidence.

## Corrections exposed by the canary

1. The old default model was unsupported. Workers now use explicit requested
   models with high reasoning and disabled fallback.
2. AWF's old model catalogue rejected the new model for accounting. The pinned
   newer runtime accepts explicit positive accounting rates and retains finite
   budgets. See [runtime details](Worker-runtime.md).
3. gh-aw implicitly enabled issue creation. All publication paths are now
   disabled and checked during compilation. The three startup-generated issues
   were closed without comments.
4. The fix result example now specifies a nonempty array of test-description
   strings, matching the strict collector schema.
5. gh-aw's human membership check skipped `github-actions[bot]`. The worker
   configuration now allows that exact bot while retaining human role checks.
6. Full-suite evidence needs to account for xUnit theory rows expanded during
   execution. Discovery lists test definitions; multiple executed cases can share
   a definition or rendered name. This must be handled without losing cases or
   confusing unchanged failure counts with unchanged failures.

## First collected candidate

- Canary v2 stopped before a model request: the shipped Codex CLI rejected
  `agents.enabled=false`. Offline checks against the actual installed CLI verified
  the replacement flags `features.multi_agent=false` and
  `features.multi_agent_v2=false`.
- [Canary v3's fix worker](https://github.com/litedb-org/LiteDB/actions/runs/34980481504)
  completed and produced candidate `ed795329beeec37c92dc8eb11e1c4086e9fd14ee`.
  It changes only the ObjectId constructor's argument guard. All regression
  source remains unchanged.
- Its [baseline](https://github.com/litedb-org/LiteDB/actions/runs/34980331557)
  confirmed the expected red cases, and its
  [focused check](https://github.com/litedb-org/LiteDB/actions/runs/34981481193)
  passed. Its broad check stopped on four #2870 failure-classification changes,
  which are under investigation. This is not an accepted fix.
- The worker config explicitly sets `model_reasoning_effort = "high"` and request
  usage confirms `gpt-6-astra`. However, the installed CLI does not recognize the
  model alias and its fallback metadata disables reasoning support. Source
  inspection found that this can omit reasoning from requests despite the
  configured effort. The controller was stopped before acceptance. The user
  requested Codex 0.154.0; the replacement runtime must verify the actual outgoing
  reasoning setting before a fresh canary can be accepted.
- The first full capture omitted build jobs because GitHub rejected a numeric
  issue input at the reusable-workflow boundary. Matching string input types
  restored build-job expansion in the
  [corrected isolated probe](https://github.com/litedb-org/LiteDB/actions/runs/34981833553).
- The initial #2794 Windows historical-package timeout passed one targeted retry.
  Both original failure and retry evidence are retained. The incomplete capture
  and duplicate artifact names cannot satisfy acceptance; a fresh complete
  baseline remains required.

## Codex 0.154.0 restart

The user requested Codex 0.154.0 after identifying that GitHub showed only the
model name. Tests using the actual new binary captured requests for both Astra
and Sol with `reasoning.effort: high`; the old alias fallback no longer suppresses
the setting. Commit `67f9f77c3ffa7be39f480a5f86ab37a549efc2a1` pins this version,
verifies both requests before inference, and records a trusted proof and readable
GitHub summary. The [new runtime canary](https://github.com/litedb-org/LiteDB/actions/runs/34983508910)
finished successfully. Its downloaded proof confirms Codex 0.154.0 and high
reasoning for both models on the actual GitHub runner.

[Campaign v4](https://github.com/litedb-org/LiteDB/actions/runs/34984313324) uses
immutable runtime commit `7629236e73bde318e934509244c3e61ef820d8db`. All 205
automation tests passed before dispatch. Its new baseline still uses frozen
source `dd937719f7eee53c512f50ac604cab639bf42a4c`.

V3 is durably blocked at state commit
`dfd372c9371f3ff99c9990c907a57bbf9ccf5bb4`; its controller and second repair worker
were cancelled. No candidate from that runtime was integrated.

The four #2870 classification changes were confirmed as checkout-root and
test-library frame variation. The gate now retains the complete stack assertion
and normalizes only reviewed presentation differences. Other failure drift
remains rejected and routes to investigation. The paired 1,509-test evidence
passes with this reviewed grading policy. All 33 artifacts from the isolated
build probe also pass evidence validation, including exact #2874 red cases in
all 21 full-suite lanes. The [fresh full baseline](https://github.com/litedb-org/LiteDB/actions/runs/34982194439)
has all 109 expected jobs and is still being monitored.

## Original acceptance requirement (superseded below)

A successful pilot requires the verified candidate, focused and broad CI, three
independent Sol high reviews, platform and compatibility checks, the original
full matrix, and an atomic integration update to the tested commit. Existing
unverified process repros cannot be counted as passing compatibility evidence.
Scale-up starts only after that pilot is accepted.

This paragraph records the original rollout requirement. The later compressed
sweep requirement replaces it: the full matrix is required only at the end.

The #2854 process fixture currently fails compilation: `Issue2854Scenario.cs:108`
calls `RawPageListFixture.InspectHealthyEmptyList`, which is absent from the
frozen source. Its package and current variants both report that they did not
execute. This is a harness defect, not an observed database behavior, and cannot
be admitted as a known failing regression.

The user explicitly authorized temporarily disabling compilation blockers on
2026-09-15. The campaign therefore quarantines exactly the three #2854 process
jobs while preserving their source unchanged. The remaining original matrix
must still complete with exact baseline/candidate evidence. This coverage gap
must accompany integration evidence; #2854 remains unverified and is not counted
as fixed. Restoring its jobs requires a separately reviewed harness correction.


## V4 candidate and validation pause

Candidate `79e5c69cb9e6a1bcc08e919c2993aaa5401148b7` adds two argument guards to
`ObjectId(byte[], int)`. The original regression tests remain unchanged.
The [baseline](https://github.com/litedb-org/LiteDB/actions/runs/34984349375)
confirmed six failing regressions and one passing control;
[focused CI](https://github.com/litedb-org/LiteDB/actions/runs/34985300364)
passed all seven, and
[broad CI](https://github.com/litedb-org/LiteDB/actions/runs/34985454867)
accepted all 1,509 cases against the baseline failure ledger.

All three independent Sol high reviews passed without findings:
[behavior](https://github.com/litedb-org/LiteDB/actions/runs/34985735409),
[compatibility](https://github.com/litedb-org/LiteDB/actions/runs/34985757907), and
[lifecycle](https://github.com/litedb-org/LiteDB/actions/runs/34985781861).
The compatibility reviewer also passed current/5.0.21 ordinary and encrypted
file checks. This does not replace the required acceptance compatibility job.

[Acceptance](https://github.com/litedb-org/LiteDB/actions/runs/34988724926)
passed Ubuntu net8 and both Windows lanes. Ubuntu net10 and both macOS lanes
rejected only the first-source #2870 failure classification: unchanged source
produces different JIT iterator and wrapper frames. Its compatibility job was
skipped, so acceptance is incomplete. The controller inspected only the Ubuntu
net8 artifact when classifying failure and dispatched an unnecessary retry.
Both controller and retry were cancelled; a legitimate pause event at state
commit `7ef44174c8b9cb38b5449e0689e1c957e2b85f5f` preserves the candidate,
reviews, and failed run. A reviewed revalidation path must bind a new immutable
check definition separately from the original worker/reviewer runtime.

The explicit [#2794 timing overlay](Harness-2794.md) passed in all six paired
full-matrix jobs. The subsequent paired capture exposed a separate historical
#2825 harness classification gap: an aggregate contains the expected primary
failure plus an unrecognized concurrent-write secondary stack. Its current
variant passes. These raw artifacts remain failures; the harness correction
requires explicit provenance and fresh paired evidence. No candidate has been
integrated, and scale-up remains pending.


## Compressed sweep requirement

The user clarified on 2026-09-15 that the full pipeline must run only after the
entire sweep. This supersedes the pilot plan's full-matrix requirement before
each integration merge. The revised plan uses baseline confirmation, one
combined focused/broad candidate CI run with diff-selected extra checks, three
independent reviews, and direct integration of the unchanged tested commit.
There is no separate post-review candidate rerun. The original full pipeline is
a distinct final-promotion gate across all accepted issues.

Both full pilot runs completed: baseline `34986503761` and candidate
`34986507516`. The final comparison covers all 105 paired evidence artifacts:
33 ordinary/cross-process test artifacts and 72 repro artifacts. All compare
consistently except the documented historical #2825 harness error. The three
#2849 performance pairs also match. These runs remain diagnostic evidence;
no new full matrix is scheduled to retest the reviewed harness correction.

The reviewed first-source #2870 normalization is committed at `282a999e` with
policy digest `d4c510bf1504ec8298e0f231548fde8e67a929f7a3ccca9c6ec21a1a89571a36`.
Replaying the original six acceptance pairs under that policy produces matching
ledgers, while their original failed reports remain preserved. The compact
profile planner selects Ubuntu/net8 plus a production build for candidate
`79e5c69cb9e6a1bcc08e919c2993aaa5401148b7`. Audited targeted revalidation is still
required before accepting this paused canary because its original broad run
did not include the complete new profile's production-build evidence.

## Compressed canary passed

The audited revalidation used immutable check definition
`807054e37840ab31184642a86c4bbb7c15983de8` on
`automation/bugfix-runtime-v5`, preserving the same candidate and all three
original reviewer reports. [Run 34995923000](https://github.com/litedb-org/LiteDB/actions/runs/34995923000)
passed with exactly three jobs:

- Profile selection: 9 seconds.
- Ubuntu/.NET 8 test lane: 2 minutes 40 seconds, including baseline/candidate
  builds, focused assertions, and 1,509 broad-suite cases.
- Production build: 39 seconds, running in parallel with the test lane.

Job execution spanned 2 minutes 52 seconds overall. All seven issue cases passed;
the broad comparison reported zero new errors, classification changes,
inconclusive changes, or unexpected passes. The 497 remaining known failures
are unresolved baseline defects, not passing tests. The ordinary ObjectId diff
does not require an additional file-compatibility job under the reviewed profile.

The controller recorded `ready` at state commit
`41f882453716307261f7e0fd6f157d5c1066760c`. The independent integration preview
verified the exact candidate tree and 20 evidence files. No full-matrix job was
dispatched for this revalidation.

Integration completed successfully: `integration/bugfixes` now points to
`79e5c69cb9e6a1bcc08e919c2993aaa5401148b7`, and the accepted ledger permanently
requires all seven #2874 cases. Its `final_matrix_status` is explicitly `pending`.
Campaign `sweep-2839-v1` then started on this integration base, using the same
immutable runtime v5 and the new `compressed-v1` protocol.

## Windows metadata encoding correction

The first #2839 baseline, [34996458134](https://github.com/litedb-org/LiteDB/actions/runs/34996458134),
exposed a controller bug after integration. All seven required-pass cases passed,
but five stored theory identities contained `Â·` instead of the original `·`.
The local Windows controller decoded `git show` through CP1252 when constructing
the accepted contract. Campaign v1 was paused and retry `34996606840` cancelled.
This was metadata corruption, not a regression in the ObjectId candidate.

Runtime v6 (`ebf413d240e747ead603d5076bdff6d09a35ccf3`) explicitly decodes Git and
GitHub command text as UTF-8. Tests simulate a legacy host code page. The reviewed
`repair_ledger_encoding.py` authenticated the immutable original contract, required
the exact derived corruption, rechecked all CI and reviewer evidence, and repaired
only the two accepted-name lists. Original artifacts and the original stored
contract remain intact. The correction adds a separate contract and audit under
`evidence/canary-2874-v4/encoding-correction-b530fc144bd2/` at controller state
`ff3701819065d1682a3147e201a50091a0a1c206`. The audit SHA-256 is
`853b014befe29c0f8d67e3f5a54dbde340bbcc3a236c1ac27d0c0cad69502e5f`.

Campaign `sweep-2839-v2` starts from the unchanged integration commit and a fresh
corrected ledger snapshot under immutable `automation/bugfix-runtime-v6`.
The runtime also routes verified candidate C# compiler failures back to repair
and includes the reviewed next-three issue contracts. Combined local checks
passed: 149 controller tests, 74 gate tests, and 76 workflow/helper tests.
