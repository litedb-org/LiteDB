# Manual sweep (2026-09-16)

The user's latest instructions supersede the hosted execution plan: manually fix
reproduced reports from PR #2885 in live priority-label order, obtain four
independent GPT-5.6 Sol reviews, resolve justified findings, then commit and push
each fix before selecting the next. P1 precedes P2 and P3.

GitHub controls were stopped before manual work: `BUGFIX_SWEEP_ENABLED=false`,
`bugfix-sweep.yml` is `disabled_manually`, and active fixer run 35084716213 is
confirmed `completed/cancelled`. Historical hosted journals and candidate branches
remain intact. Do not resume the hosted scheduler during the manual sweep.

Branch: `codex/manual-wholesale-bugfix`. It merges the automation documentation
head 7e09ef1d and accepted integration bbb0253b, preserving #2874, #2839, #2869,
and #1506. The unfinished #1002/#2811/#2590 candidate was improved locally,
passed 35 focused cases, and is preserved in the named local git stash
`Preserve auto-ID co-repair for P2 after prioritizing P1 bugs`. It is not accepted.

`progress.json` retains the PR inventory, refreshed live labels, and explicit
current dispositions. It includes reports outside the former 24-task automation
queue; historical-only/unconfirmed cases remain distinct from current defects.
P1 order begins #2818, #2824, #2820, #2848, #2806; all have critical severity.
The complete scope remains unfinished.

## #2818 — WAL durability

Before: explicit and implicit commits only called ordinary stream Flush, allowing
acknowledgement before durable storage. Now confirmation-bearing WAL batches call
FlushToDisk before WAL-index confirmation; ordinary safepoints and empty commits
do not incur durable flushes. Encryption and caller-stream wrappers already
propagate this operation to FileStream.Flush(true).

A failed durable flush has an uncertain outcome because the confirmation may
already be persisted. The engine is closed with the original IOException (or a
wrapper retaining a non-I/O cause), preventing rollback and subsequent writes.

Validation: original regression baseline 2 failed / 1 passed; final durability
and WAL transaction boundary selection 25 passed. The broader 88-case selection
improved both #2818 cases and retained existing failures, with the known
intermittent #2814 WAL-growth case changing outcome. A focused unchanged-baseline repeat also reproduced #2814. Plain/encrypted vector
compatibility passed. Production builds passed both library targets with zero errors. Results/logs are retained under `/tmp/litedb-manual-results`
and `/tmp/litedb-2818-*.log` on this workstation.

Four independent `gpt-5.6-sol` reviews: `review_2818_1` through `review_2818_4`.
Initial review 3 found the explicit-commit flush-failure gap; it was fixed and all
four re-reviewed and approved the final implementation. Useful nits addressed:
WAL-index publication wording, update-only and post-safepoint flush counters,
empty-commit behavior. Additional filename/rollback-return-specific tests were
not added: they use the same already-covered FileStream/confirmed-header path,
and existing recovery tests cover their distinct functional behavior.
