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
must finish successfully before restarting the fix campaign.

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

## Acceptance still required

A successful pilot requires the verified candidate, focused and broad CI, three
independent Sol high reviews, platform and compatibility checks, the original
full matrix, and an atomic integration update to the tested commit. Existing
unverified process repros cannot be counted as passing compatibility evidence.
Scale-up starts only after that pilot is accepted.

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
