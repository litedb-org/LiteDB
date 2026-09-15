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
