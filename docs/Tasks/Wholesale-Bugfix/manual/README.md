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

## #2824 — rebuild encryption and collation

Omitted API/SQL options preserve the current password and collation. Supplied
options select a new password (null removes encryption), while absent collation
preserves the current one. Effective settings are propagated to the reused
Direct/Shared settings only after installing the rebuilt file. Size metadata is
read before file moves so it cannot fail between installation and propagation.
Caller-owned options are not mutated.

Validation: the original ten cases failed before the fix. The SQL test had a
redundant FluentAssertions NotBeOfType assertion that itself fails on successful
null results. Removing it retains the stronger BeNull requirement and every
ledger/password/byte oracle. The corrected fixture still fails all ten cases on
unchanged baseline 25ef5063. The final selection passes 24 cases with one existing
skip, including eight new custom-collation Direct/Shared cases, ordinary rebuild
coverage and vector-format rebuild checks. Production builds pass both targets.

All four Sol reviewers re-reviewed and approved. Addressed findings: preserve
custom collation during password-only rebuild; remove fallible post-replacement
metadata reads; expand case-sensitive-key coverage and public XML documentation.
A proposed tri-state password-options redesign was refuted and the reviewer
agreed: supplied null Password explicitly removes encryption under #2824's
retained contract. This does not assert general crash-atomicity of file swaps.

## #2820 — validate files before repair

Length queries no longer mutate files or caller streams. Existing database
headers are fully validated before writable tail alignment; read-only alignment
only limits logical reads. Factory AES opens reject partial/zero-check encrypted
metadata instead of using the internal stream's legacy repair behavior. Header
identity/version validation precedes decoding arbitrary creation-time bytes.

Validation: corrected baseline 9 failures / 2 passing controls; final candidate
215 passed, including plain/encrypted torn tails, caller-stream byte preservation
and ownership, explicit v4/read-only upgrades, stream lifecycle and vector-format
coverage. Production builds and plain/encrypted vector compatibility pass.

Two expected v4 GUID literals in the frozen fixture test were demonstrably
incorrect (one even had invalid length). A standalone NuGet LiteDB 4.1.4 program
read the unchanged SHA-pinned encrypted fixture and returned
`4ac8f759-248f-4114-8be6-e510ad4e140d` (Jesse) and
`db503008-84d5-42d8-b372-d7616ea133f1` (Bob). Those exact literals were corrected;
all hash, byte, rejection, explicit-upgrade, sentinel and reopen checks remain.
Oracle program/output: `/tmp/litedb-2820-v4-oracle/`. Corrected tests still produce
the same 9 baseline failures. Four Sol reviewers approved the production changes
and independently checked the oracle correction and added coverage. No blocking
findings remained.

## #2848 — terminal transaction completion failures

Commit/rollback completion and monitor-release failures stop the engine, drain
transactions/resources, and preserve the first fatal exception atomically.
Later operations reject immediately, including while cleanup is still running.
Automatic error handling rolls back only active transactions. This also fixes
the reproduced #2169/#2803 post-commit checkpoint-error masking; it does not
claim confirmation of #2803's original SynchronizationLockException report.

Baseline: all three original #2848 cases fail. Final selection: 47 passed across
original issue cases, non-I/O failures, first-cause concurrency, frame cleanup,
WAL recovery/durability and checkpoint errors. Production builds pass both targets.
The older WAL boundary test's rollback-after-failed-Commit expectation was
updated to fatal rejection, retaining and strengthening all frame, WAL length,
monitor, independent recovery and checkpoint oracles. The issue regression now
requires exact original exception identity on subsequent writes. Four Sol
reviewers approved after addressing first-cause publication races and these
lifecycle expectations. New tests additionally cover non-I/O completion failures.
