# Bugfix controller primitives

These modules implement one issue's deterministic state machine and persist its
state on `automation/bugfix-state`. Run them from trusted controller code with
`gh` authenticated for the selected repository. No agent should write this branch.

```powershell
python -m unittest discover -s .github/bugfix -v
python .github/bugfix/controller.py --repo litedb-org/LiteDB --campaign canary-2874 init --issue 2874 --base-sha FULL_SHA --test-source-sha dd937719f7eee53c512f50ac604cab639bf42a4c --workflow-sha FULL_SHA
python .github/bugfix/controller.py --repo litedb-org/LiteDB --campaign canary-2874 get
python .github/bugfix/controller.py --repo litedb-org/LiteDB --campaign canary-2874 record --event-json event.json --allow-workflow .github/workflows/bugfix-check.yml
```

`init` and `record` write the data branch. `get` is read-only. Every write uses an
exact expected-ref lease. A concurrent write fails; callers must reread and
reconsider the event, never blindly force-push. Duplicate identical events return
the existing state without a write. Event IDs reused with different content fail.

## Event contract

Every event includes `schema_version: 1`, a unique `event_id`, `kind`, and the exact
`campaign`, `issue`, `base_sha`, `test_source_sha`, and `workflow_sha` from state.
Every result additionally includes `candidate_sha` (`null` for baseline), numeric
`run_id`, `environment`, `artifact` (Actions artifact name), and `outcome`.

| Kind | Allowed phase | Success outcome | Resulting phase |
| --- | --- | --- | --- |
| `baseline` | baseline | `bug_present` | repairing |
| `candidate` | repairing | New full `candidate_sha`; no outcome | focused |
| `focused` | focused | `behavior_correct` | broad |
| `broad` | broad | `pass` | reviewing |
| `review` | reviewing | `pass` plus empty `findings` | acceptance after all roles |
| `acceptance` | acceptance | `pass` | ready |
| `integrated` | ready | Matching `expected_base_sha` and `integration_sha` | integrated |

Review roles are `behavior`, `compatibility`, and `lifecycle`, each from a separate
run. Review failures require nonempty `findings`. Failed checks and reviews return
to repair; three proposed candidates exhaust the repair budget. `harness_error`
allows two retries per stage/role independently of code repairs; a third blocks.
`inconclusive` blocks immediately. `pause` and `resume` are explicit operator
events. New candidates discard active candidate approvals while preserving the
complete event history.

For CI/review results, `record` verifies the GitHub run's exact workflow commit,
allowlisted workflow path, dispatch trigger, completion, and artifact existence.
Positive evidence requires a successful run and a downloaded, validated payload:
CI `verdict.json` must match every pinned identity, environment, level, and expected
outcome. Candidate CI also requires accepted `scope.json` with matching provenance.
Review `result.json` must match the candidate and role, approve without findings,
and describe concrete coverage; collector metadata must match the run and report
digest. Bounded ZIP inspection reads reports without extracting archive paths.
Supply multiple `--allow-workflow` arguments as needed.

The controller computes and persists report/archive SHA-256 digests; callers cannot
supply these fields. Identical previously recorded events remain idempotent without
redownloading expired artifacts. The trusted CI grader still owns interpretation
of selected tests, assertions, controls, and regression ledgers; a caller's claimed
positive outcome cannot substitute for its downloaded verdict.

`integrated` records a completed integration update; it does not perform that
update. The integration workflow must serialize merges, check the current branch
against `expected_base_sha`, and atomically advance it to the already tested
candidate commit before recording this event. A new base requires a new campaign
and renewed evidence; these primitives never rewrite pinned identities.

Queue concurrency, campaign-wide run/cost limits, issue eligibility, environment
selection, artifact retention, branch permissions, and actual integration are
responsibilities of the owning workflows. These primitives do not schedule agents
or dispatch arbitrary issue reports.
