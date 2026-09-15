# Integrate a validated fix

The separate integration CLI consumes a campaign that has reached `ready`. It
does not dispatch agents or amend candidate code.

```powershell
python .github/bugfix/integrate.py --repo litedb-org/LiteDB --campaign CAMPAIGN --candidate-sha CANDIDATE_FULL_SHA
```

The default command verifies evidence without updating remote branches. Add
`--apply` to advance `integration/bugfixes` after verification succeeds. All SHA
arguments must be full immutable commits. The explicit candidate must exactly
match the ready campaign.

## Per-fix validation and final promotion

The integration CLI reads the campaign's immutable runtime, accepted-test
snapshot, exact candidate profile and authenticated evidence. It reproduces the
profile from the trusted runtime and candidate diff. One compressed candidate CI
workflow supplies focused tests, ordinary regression comparison, production
builds and the selected compatibility/platform checks. All three independent
reviews must approve that same candidate, with no findings or only cosmetic nits.
No unchanged post-review check or original full-matrix run is required here.

Run `final_promotion.py` only after the eligible sweep and remaining report
dispositions are complete. Its separate capture/grading arguments are documented
by `python .github/bugfix/final_promotion.py --help`; they are not arguments to
`integrate.py`. Final validation checks the complete accepted chain and original
matrix. The exactly three compile-blocked #2854 process jobs remain explicitly
quarantined and unverified, never accepted as fixed.

## Acceptance and durable evidence

Before updating integration, the CLI downloads and revalidates the campaign's
baseline, required candidate CI lanes, production evidence and all three review
artifacts. Required compatibility checks must have completed successfully.
Report hashes must match those previously accepted by the controller; missing,
changed or expired evidence cannot become a positive verdict.

The candidate must descend from the recorded integration base, and its local tree
must equal the remote immutable commit's tree. The branch advances directly to
that exact tested commit; the tool does not create a new merge commit.

Hosted candidate creation uses the immutable parent's timestamp as a reproducible
commit stamp, not as wall-clock execution time. The same validated patch, parent
and message recreate the same commit after a runner loss. The candidate is pushed
and its exact remote ref read back before its SHA is journaled. A conflicting
remote candidate is never adopted or overwritten. Actions and journal timestamps
provide the actual execution chronology.

The controller-owned `automation/bugfix-state` branch stores:

- A persistent global `integration-lock.json` that serializes integration work.
- Exact per-fix artifact ZIPs and API metadata under
  `evidence/CAMPAIGN/`, plus test contracts and SHA-256 evidence manifests.
- `accepted-tests.json`, retaining previously accepted issue contracts and adding
  the new permanently passing regression/control identities.
- The completed campaign state, tested tree, selected validation profile and
  provenance, with final-matrix status recorded as pending.

Evidence is committed before the integration branch changes. Every data-branch
commit and the integration-branch update use exact expected-ref leases. A moved
base requires renewed evidence; the CLI does not automatically rebase or replace
another writer's changes. Evidence is bounded at 512 MiB per data commit and
95 MiB per file; exceeding a bound stops before integration.

## Recovery

If the process stops after acquiring its persistent lock, rerun the exact command
with `--apply --resume`. The candidate, runtime/profile and acceptance run
must match the locked transaction. There is no automatic lock
expiration or takeover by another campaign.

If the branch update completed but the final state commit did not, an explicit
resume can finish recording the same prepared candidate without pushing it again.
The command revalidates evidence on resume. While the transaction is only
`acquired`, it may still need Actions artifacts. Once `prepared`, its lock binds
the exact candidate tree, archive prefix and evidence-manifest digest. Resume
reads the bounded raw archives from the immutable state-branch snapshot and
revalidates their run, report, artifact, profile and reviewer identities before
advancing or finishing. It does not depend on Actions retention after preparation.
A changed campaign, conflicting candidate or evidence mismatch still blocks;
neither retry nor archive recovery bypasses validation. A conflicting state-branch
write stops the current attempt rather than overwriting another writer.

Run the integration tests with the rest of the controller suite:

```powershell
python -m unittest discover -s .github/bugfix -v
```
