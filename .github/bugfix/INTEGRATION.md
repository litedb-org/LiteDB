# Integrate a validated fix

The separate integration CLI consumes a campaign that has reached `ready`. It
does not dispatch agents or amend candidate code.

```powershell
python .github/bugfix/integrate.py --repo litedb-org/LiteDB --campaign canary-2874 --candidate-sha CANDIDATE_FULL_SHA --baseline-run BASELINE_RUN_ID --candidate-run CANDIDATE_RUN_ID --evidence-definition-sha CAPTURE_FULL_SHA --grading-policy-sha POLICY_FULL_SHA
```

The default command verifies evidence without updating remote branches. Add
`--apply` to advance `integration/bugfixes` after verification succeeds. All SHA
arguments must be full immutable commits. The explicit candidate must exactly
match the ready campaign.

## Separate capture from grading

`--evidence-definition-sha` identifies the workflow that captured the original
matrix baseline and candidate runs. `--grading-policy-sha` identifies the trusted
collector, comparator, and normalization policy used to evaluate those artifacts.
The CLI executes those tools from a detached checkout of the grading commit and
requires the run evidence to authenticate against the capture commit. It records
both commits, grading-script hashes, and the comparator report digest.

The collector downloads real GitHub run metadata, jobs, and artifact archives for
both runs. The comparator runs locally against that evidence. Caller-authored
verdict JSON is never an acceptance input.

The current reviewed quarantine removes exactly the three compile-blocked #2854
ReproRunner jobs. The trusted `.github/bugfix/full-ci-quarantine.json` is bound by
hash to capture and grading, and the comparator must account for all 109 remaining
jobs and 21 target-test jobs. The report and permanent acceptance ledger preserve
this coverage gap as `unverified`. This does not accept #2854 as fixed. Other missing
jobs, harness errors, unreviewed outcome changes, and inconclusive results block
integration.

## Acceptance and durable evidence

Before updating integration, the CLI downloads and revalidates the original
baseline, focused/broad checks, all six acceptance matrix artifacts, and all three
independent reviewer artifacts. The compatibility job must have completed
successfully. Report hashes must match the reports previously accepted by the
controller; changed or expired evidence blocks the operation.

The candidate must descend from the recorded integration base, and its local tree
must equal the remote immutable commit's tree. The branch advances directly to
that exact tested commit; the tool does not create a new merge commit.

The controller-owned `automation/bugfix-state` branch stores:

- A persistent global `integration-lock.json` that serializes integration work.
- Exact acceptance and original-matrix artifact ZIPs and API metadata under
  `evidence/CAMPAIGN/`, plus test contracts and SHA-256 evidence manifests.
- `accepted-tests.json`, retaining previously accepted issue contracts and adding
  the new permanently passing regression/control identities.
- The completed campaign state, tested tree, matrix provenance, and any explicit
  coverage gaps.

Evidence is committed before the integration branch changes. Every data-branch
commit and the integration-branch update use exact expected-ref leases. A moved
base requires renewed evidence; the CLI does not automatically rebase or replace
another writer's changes. Evidence is bounded at 512 MiB per data commit and
95 MiB per file; exceeding a bound stops before integration.

## Recovery

If the process stops after acquiring its persistent lock, rerun the exact command
with `--apply --resume`. The candidate, capture/grading commits, and original
matrix run IDs must match the locked transaction. There is no automatic lock
expiration or takeover by another campaign.

If the branch update completed but the final state commit did not, an explicit
resume can finish recording the same prepared candidate without pushing it again.
The command revalidates evidence on resume. Expired artifacts or a changed campaign
state require an operator to inspect the durably preserved evidence; they never
cause the tool to silently bypass validation. A conflicting state-branch write
also stops the current attempt rather than overwriting it.

Run the integration tests with the rest of the controller suite:

```powershell
python -m unittest discover -s .github/bugfix -v
```
