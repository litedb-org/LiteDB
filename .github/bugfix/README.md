# Bugfix controller primitives

## Run or resume a canary

```powershell
python .github/bugfix/orchestrate.py --repo litedb-org/LiteDB --campaign canary-2874 --issue 2874 --integration-base BASE_FULL_SHA --workflow-sha WORKFLOW_FULL_SHA --workflow-ref automation/wholesale-bugfix --dry-run
```

Replace the SHA placeholders with full immutable commits. Remove `--dry-run` to
dispatch workflows, persist campaign state, and publish restricted candidate
branches. Run the same command again to resume. The workflow branch must still
point to the pinned workflow SHA before every new dispatch. The regression source
is fixed at `dd937719f7eee53c512f50ac604cab639bf42a4c`.

The orchestrator confirms baseline failure, requests a restricted fix, applies it
in an isolated worktree, verifies its scope and C# sizes, publishes a unique
`fix/issue-N-CAMPAIGN-aK` branch, and runs focused/broad checks. Three independent
reviewers run concurrently. Acceptance requires all six platform/framework
artifact verdicts plus the production-build and file-compatibility job. It stops
at `ready`; final full-matrix validation and integration remain separate actions.
There is no automatic merge or `--integrate` option in this version.

### Preserve fixes already integrated

New campaigns snapshot `accepted-tests.json` at the exact controller data-branch
commit observed during initialization. The snapshot records canonical ledger and
test-case hashes and requires every previously accepted candidate to be an ancestor
of the selected integration base. Campaign initialization fails if that ancestry
or the frozen regression source does not match.

Check dispatches carry `accepted_state_sha` and `accepted_ledger_sha256` from this
immutable snapshot. Trusted grading code reads the ledger from that Git commit;
workers do not supply or edit the required passing cases. Baseline/focused checks
run a separate `required-pass.trx` selection after the same build, preserving the
current issue's exact focused-test selection. Broad/acceptance checks enforce those
identities in both full baseline and candidate reports before constructing the
known-failure ledger. Missing, skipped, or failed accepted cases stop validation
even if their classes are on the unresolved-failure allowlist.

Every positive CI verdict binds the snapshot and confirms that permanent passing
contracts were enforced. An empty ledger is explicit and still pinned to a data
commit. The controller data branch must already exist before initializing this
runtime. Existing campaigns without `passing_contract` must resume with their
original pinned runtime; the new runtime never silently adds a snapshot or changes
the acceptance rules of a live campaign.

Workers use `gpt-6-astra` for fixes and `gpt-5.6-sol` for reviews, both with high
reasoning effort. Collector configuration must match; differing reported runtime
models or reasoning levels are rejected when that runtime evidence is available.
Repair workers receive bounded structured diagnostics from failed checks and
review findings. A later attempt starts from the preceding candidate while the
original integration base and regression source remain pinned.

The data branch also stores the dispatch journal. A controller-generated UUID in
`request_id` links each operation to its exact workflow run. The journal is saved
before dispatch. If a process dies during dispatch and no matching run can be
found, the controller records a blocker rather than risking a duplicate worker.
Inspect GitHub and the recorded request before resolving that condition manually.
One issue permits three candidate attempts, two infrastructure retries per stage,
and at most 40 workflow runs. Individual workflow waits are bounded at 180 minutes;
the process prints run URLs and status changes while monitoring.

Malformed or stale acceptance evidence blocks the campaign. Missing/incomplete
test execution counts as infrastructure failure; completed selected assertions
that still fail return to repair. Ready campaigns resume without dispatching more
work. A changed integration base or workflow revision requires a new campaign and
renewed evidence. The controller does not change pinned identities to resume.

## State CLI

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

`orchestrate.py` schedules only an explicitly selected issue with a reviewed
execution contract. Cross-issue queue concurrency, monetary cost limits, artifact
retention, branch permissions, and actual integration remain responsibilities of
the owning workflows. The CLI enforces its per-issue workflow-run budget and
does not dispatch arbitrary issue reports.
