# Wholesale bugfix sweep: agent handoff

Tracking issue: [litedb-org/LiteDB#2890](https://github.com/litedb-org/LiteDB/issues/2890).
Repository copy: [HANDOFF.md](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/docs/Tasks/Wholesale-Bugfix/HANDOFF.md).

Snapshot: **2026-09-15, approximately 20:00 UTC / 22:00 Europe/Vienna**.
Re-read GitHub state before acting: this document is a snapshot, not the controller's state store.

## Current state and first action

**Four fixes are integrated. The serial queue has stopped at #1002, attempt 2,
because its repair changed eight existing #2811 failures. No #1002 candidate is
accepted. No controller was running when this snapshot was taken.**

First investigate the #1002/#2811 overlap below. Do not simply redispatch the
queue, clear its blocker, broaden an allowlist, or claim its second candidate is
green. Preserve the failed runs and the first lifecycle review finding. This is
a real behavioral overlap requiring an explicit repair/contract decision.

The previous local exec session `13996` exited with code 1 after recording the
block. Session IDs are not portable; inspect local processes and GitHub before
starting any replacement controller. Nothing will advance while this blocker
remains unresolved.

## Key branches and immutable revisions

Repository: **litedb-org/LiteDB**, accessed with `gh ... -R litedb-org/LiteDB`.
Local workspace: `C:\Users\Jonas\repos\private\JKamsker\LiteDB`.
Remote `upstream` is litedb-org/LiteDB; `origin` is JKamsker/LiteDB.

| Purpose | Branch / source | Snapshot revision |
| --- | --- | --- |
| Accepted production fixes | [integration/bugfixes](https://github.com/litedb-org/LiteDB/tree/integration/bugfixes) | `bbb0253bc06324f0bb14a21a727a37e8c7f2b213` |
| Durable campaign journal, accepted tests, evidence, integration locks | [automation/bugfix-state](https://github.com/litedb-org/LiteDB/tree/automation/bugfix-state) | `0106f6321a051c1b5a63613b681476009d612230` |
| **Live campaign's immutable runtime** | [automation/bugfix-runtime-v8](https://github.com/litedb-org/LiteDB/tree/automation/bugfix-runtime-v8) | `61ee4aec705024c189345de25508ea19ffc2584e` |
| Future automation code and preparation | [automation/wholesale-bugfix](https://github.com/litedb-org/LiteDB/tree/automation/wholesale-bugfix) | `db0c0ea910d7e46c655cb1e21826d035503b73ba` before this handoff commit |
| Rejected #1002 attempt 1 | [fix/issue-1002-expansion-v8-1002-a1](https://github.com/litedb-org/LiteDB/tree/fix/issue-1002-expansion-v8-1002-a1) | `a98b29089c01e21e50856abd0cc9bbea1ecc5631` |
| Blocked #1002 attempt 2 | [fix/issue-1002-expansion-v8-1002-a2](https://github.com/litedb-org/LiteDB/tree/fix/issue-1002-expansion-v8-1002-a2) | `d600cab9aa8be419f0d3069a21bb5e03ff713307` |
| **Frozen regression source** from codex/implement-regression-tests-for-all | [exact original tree](https://github.com/litedb-org/LiteDB/tree/dd937719f7eee53c512f50ac604cab639bf42a4c) | `dd937719f7eee53c512f50ac604cab639bf42a4c` |

Do not move immutable runtime refs. The mutable automation branch has changes
that are **not deployed to v8**; never run a live campaign under those definitions
without an explicit reviewed transition. Fixes accumulate only on the integration
branch. Final promotion to a release/development branch has not happened.

## Accepted fixes

| Issue | Accepted candidate | Required permanent cases added |
| --- | --- | --- |
| #2874 ObjectId byte-window guards | `79e5c69cb9e6a1bcc08e919c2993aaa5401148b7` | 7 |
| #2839 LongCount preserves Int64 result | `824dfce9d27a6c0c7ea3890c1ecc9aff31833c83` | 3 |
| #2869 Int32 numeric widening | `781b3291ee0d6aa7a08e05955545fdf4d03763dc` | 6 |
| #1506 paging overrides do not mutate caller Query | `bbb0253bc06324f0bb14a21a727a37e8c7f2b213` | 2 |

There are **18 permanent passing cases**. The accepted ledger at data commit
`f6d5a4caae1fdba1737bb38161c34693d7b68db6` records these four fixes; later data
commits also contain the active campaign history. Final matrix status is pending.
Detailed run IDs and limitations are in the
[canary log](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/docs/Tasks/Wholesale-Bugfix/Canary-log.md).

## Immediate blocker: #1002 repair overlaps #2811

Campaign [expansion-v8-1002.json](https://github.com/litedb-org/LiteDB/blob/automation/bugfix-state/expansion-v8-1002.json):
phase `blocked`, paused `false`, repair_attempts `2`, original integration base
`bbb0253bc06324f0bb14a21a727a37e8c7f2b213`. The top-level blocked_reason is null;
the actual explanation is in `evidence.broad` and the retained history.

1. [Baseline 35009017612](https://github.com/litedb-org/LiteDB/actions/runs/35009017612)
   established one red nullable-Int32 auto-ID case and one passing zero-ID control.
2. [First fixer 35009168160](https://github.com/litedb-org/LiteDB/actions/runs/35009168160)
   added `id.IsNull` to the Int32 empty-ID predicate in `Collections/Insert.cs`.
   [Candidate CI 35010144390](https://github.com/litedb-org/LiteDB/actions/runs/35010144390)
   passed; test lane 2m57s, production/compatibility 1m14s in parallel.
3. [Behavior 35010513878](https://github.com/litedb-org/LiteDB/actions/runs/35010513878)
   and [compatibility 35010539971](https://github.com/litedb-org/LiteDB/actions/runs/35010539971)
   passed. [Lifecycle 35010564817](https://github.com/litedb-org/LiteDB/actions/runs/35010564817)
   reproduced a **major** regression: a get-only `int? Id` insert commits, then
   generated-ID copy-back throws NullReferenceException. The row survives reopen.
   Compatibility ran 173 in-repository checks, but its released 5.0.21 binary
   check was unavailable; this limitation was disclosed, not counted as passing.
4. The controller automatically dispatched
   [second fixer 35012206609](https://github.com/litedb-org/LiteDB/actions/runs/35012206609).
   It added a general `_id.Setter == null` check before `doc.Remove("_id")`, throwing
   `InvalidOperationException("Auto-generated IDs require a writable ID member.")`.
   That guard affects all auto-ID types using this helper, not just the new null case.
5. [Second candidate CI 35013331767](https://github.com/litedb-org/LiteDB/actions/runs/35013331767)
   passed the two focused cases and production job, but broad validation was
   **inconclusive**: eight #2811 tests still fail with changed failure fingerprints.
   They cover read-only ObjectId/Int32 IDs through InsertOne, InsertMany,
   UpsertOne and UpsertMany. No second-candidate reviews were dispatched.

Raw TRX inspection confirms the overlap: for read-only Int32/InsertOne, the base
fails diagnostic, row-count and file-byte atomicity assertions. Attempt 2 no
longer has those atomicity failures, but still fails the required `LiteException`
type and diagnostic checks (`Id` member name and `read-only` or `setter`). This is
partial behavioral improvement, **not harmless formatting drift** and not a full
#2811 fix. All eight changed failures have candidate canonical hash
`dde6a56c8a25d10036b186fca0c8f5446699f147f12f134d1092d3a41a575159`.

Next agent should inspect the complete frozen #2811 fixture and adjacent typed
write paths, then decide between a narrowly scoped #1002 repair or a separately
reviewed explicit behavioral co-repair contract. Any new contract needs real red
evidence, genuine positive controls and an immutable runtime/campaign identity.
An unexpected pass is also gated; merely changing the exception so other tests
turn green will not authorize unreviewed co-repair. Preserve the lifecycle
obligation through any infrastructure/contract transition and require fresh CI
and all three reviews. `resume` only clears a pause; it does not resolve this
blocked/inconclusive state. No generic blocker-reset mechanism is assumed here.

Retained local evidence (also downloadable from the linked runs):

- `artifacts_temp/bugfix-1002-v8-a2-worker/`: patch, result, collector metadata and runtime proof.
- `artifacts_temp/bugfix-1002-v8-a2-broad/`: baseline/candidate broad and focused TRXs, discovery, verdicts and pinned profile.
- `artifacts_temp/bugfix-1002-v8-review-{behavior,compatibility,lifecycle}/`: first review results/proofs.
- CI artifact `bugfix-check-ubuntu-latest-net8.0`: ID `10414383642`, ZIP SHA-256 `7b0da04ddcd26e833abe41b0f1e5c51940d3e1d386c6d5c61ab93a6439db5af1`.

## Runtime rules that must survive the handoff

- User authorized implementation, pushes, dispatch, integration, scale-up and monitoring. No repeated approval is needed for that scope. Follow repository AGENTS.md and the user's C# rules file.
- Fixer: **gpt-6-astra, high reasoning**. Three reviewers: **gpt-5.6-sol, high reasoning**. No model below gpt-5.6. Codex **0.154.0** is pinned; request probes and collected runtime evidence verify settings. Model fallback is disabled.
- The configured Actions endpoint has successfully served these workers. Credentials/endpoint values must not be copied into public issues or logs. Secrets are already configured.
- Frozen tests come from the original SHA above. Agents cannot weaken tests, delete failing cases or claim source-context guard changes as behavioral fixes.
- Only nits/no findings completes review. Any above-nit finding requires fixing **all findings including nits**. Prior obligations survive CI failures and must be verified during rereview. Missing required evidence blocks.
- One bug at a time; three review roles run in parallel. Each candidate gets **one compressed CI workflow**, with baseline/candidate focused tests, ordinary regression comparison, permanent passing cases, isolated production build and selected compatibility/platform extras.
- Known unfixed failures are allowed only under strict classification checks. New failures, changed unapproved classifications, unexpected passes, missing cases and accepted-test regressions block. Do not normalize away behavior changes.
- **No original full matrix per fix.** Run final full validation only after the eligible sweep and dispositions are complete. No unchanged post-review CI rerun is needed.
- Integration verifies exact tested commit/tree, runtime, profile, artifacts, three reviews, current-base lease and permanent ledger before moving the branch. Agents do not self-certify acceptance.
- Bounds: three candidate attempts, two infrastructure retries per stage/role, max 40 workflow runs per campaign. Inconclusive evidence stops immediately.
- Exactly three #2854 process jobs remain quarantined for the missing RawPageListFixture compilation blocker. They are unverified, not fixed. No blanket quarantine is authorized.

## Remaining queue and prepared runtime changes

Original v8 queue command (historical identity, **not a way to clear the blocker**):

```powershell
python .github/bugfix/serial_queue.py --repo litedb-org/LiteDB --workflow-sha 61ee4aec705024c189345de25508ea19ffc2584e --workflow-ref automation/bugfix-runtime-v8 --campaign-prefix expansion-v8 --issues 1506 1002 2802 2770 2867 2845 2847 2779 2205 2871 1159 1224 2858 2864 2769 2225 2322 2873
```

#1506 is accepted, #1002 blocked, and the sixteen issues after it have not started
in this queue. Preserve campaign names and journals when resuming compatible
work. Check no other controller is running before launching one. A dry-run is
read-only, but also refuses incompatible or blocked campaign state.

The mutable preparation branch has **28 approved contracts**, versus v8's 21.
The additional seven are #2746, #2807, #1829, #1444, #2860, #2870 and #2367.
These are prepared, not swept. Important changes awaiting a new immutable runtime:

- Explicit M111 UInt64 behavioral co-repair in #1224 (`dc7c03ac7c44cbe8708d9961f5d53fab7780a10e`). **Transition before starting #1224**; v8's old narrow contract will reject the related pass.
- Four wave-4 contracts (`1aeb49ad5e1447c6268d2321a660e007be689656`). #1444 requires actual old signed-order ObjectId index/Timestamp ABI evidence.
- Worker SDK absolute-path discovery instructions (`7c0e8ec5`), correcting an optional test-execution limitation observed during #1506 review.
- Environment-aware #2860/#2870 case identities/full failures plus actual VSTest testhost architecture evidence (`877f406a695bf2c686ccf368c2a3e195f7e28810`). This does not add broad normalization. #2860's max-depth portion lacks an independent positive control; the URI controls must not be represented as that control.
- #2367 (`e6d76f7d`): three captured-member regressions, two genuine controls, Ubuntu net8/net10, original/repeat evidence independently reviewed. Guard53 has no exemption. The vacuous array-navigation test is not a control.
- Combined latest checks passed **210 controller + 122 gate + 87 workflow/helper tests**. A future canary must still validate these runtime changes in GitHub.

No v9 runtime ref has been created. For a transition, drain/pause the old queue
at an explicit boundary, finish existing evidence under its original runtime,
verify no old controller remains, then start fresh campaigns on the current
integration base and ledger under the new immutable pin. Never silently rewrite
an existing campaign's runtime, test source, base or contract. The current #1002
blocker may require an explicit superseding campaign; preserve and link the old
failed history rather than deleting it.

## Other known traps and final validation

- #2871 has one precisely reviewed source-guard132 observation. It is explicitly behavior-unverified and gives no issue credit. This is not a general source-guard exemption; #2801/#2767/#2819 remain held.
- Windows CP1252 subprocess decoding once corrupted ledger names. UTF-8 and an audited ledger correction are already implemented; never repeat the old repair or resume obsolete `sweep-2839-v1`.
- Old v7 queue was deliberately drained after #2869; do not restart it.
- The final verifier retains the full accepted chain and tests all accepted cases in the original ordinary matrix. Actual testhost architecture must be verified; matrix labels alone are insufficient.
- Existing 109-job captures were pilot diagnostics, not final green validation. Final captures use the reviewed #2794 timing and #2825 classifier overlays; the frozen test source remains intact. Full-matrix completion is pending.
- There are 135 inventory reports with differing dispositions, including 99 marked reproduced. Twenty-eight prepared contracts are not the whole backlog. Historical, unconfirmed, documentation, platform and security reports need appropriate separate disposition; do not blindly run every inventory entry through a source-fix worker.
- Local gh-aw reference repos are `C:\Users\Jonas\repos\private\JKamsker\JKamsker.CodexSDK` and fork `C:\Users\Jonas\repos\external\gh-aw`. Existing compiled workflows use the reviewed fork; do not replace its compiler/runtime casually.

## Refresh and recovery references

```powershell
git status --short
git ls-remote upstream refs/heads/integration/bugfixes refs/heads/automation/bugfix-state refs/heads/automation/bugfix-runtime-v8
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'python' -and $_.CommandLine -match 'serial_queue|orchestrate.py' } | Select-Object ProcessId,CommandLine
gh api 'repos/litedb-org/LiteDB/contents/expansion-v8-1002.json?ref=automation/bugfix-state' -H 'Accept: application/vnd.github.raw+json'
gh run view 35013331767 -R litedb-org/LiteDB
```

Read the [plan](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/docs/Tasks/Wholesale-Bugfix/README.md),
[controller runbook](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/.github/bugfix/README.md),
[serial queue runbook](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/.github/bugfix/QUEUE.md),
and [canary log](https://github.com/litedb-org/LiteDB/blob/automation/wholesale-bugfix/docs/Tasks/Wholesale-Bugfix/Canary-log.md).
Run relevant checks for any changed controller/gate logic, use structured evidence
and reviewed immutable pins, push the changes, monitor actual GitHub results,
and update this tracking issue at acceptance/blocker/runtime transitions.

## Time estimate

GitHub baseline creation to last-review completion was approximately **32 minutes
for #2839, 34 for #2869 and 32 for #1506**, excluding small integration overhead
and deliberate runtime handoffs. Candidate test lanes were only 2m35s–2m57s;
agent reasoning, additional review tests and hosted-runner setup dominate elapsed time.

- For the **17 unfinished items in the current batch**, a clean first-attempt
  floor is around 9–10 hours. Budget **10–18 hours of serial execution**, plus
  engineering time for blockers/runtime transitions. The current queue is stopped,
  so this is not an unattended completion ETA.
- Completing all **24 remaining prepared contracts** is roughly **14–25 hours**
  under similar conditions, before difficult overlap/compatibility surprises.
- The full inventory will likely require **several days** and cannot yet be
  estimated reliably. Final full-matrix validation adds its own run time and any
  resulting repairs. Do not extrapolate three simple fixes into a completion promise.

## Short continuation prompt

> Continue the LiteDB wholesale bugfix sweep. Read docs/Tasks/Wholesale-Bugfix/HANDOFF.md on automation/wholesale-bugfix and refresh GitHub state first. The queue stopped at expansion-v8-1002 attempt 2 after CI 35013331767 found eight changed #2811 failures; resolve that overlap without weakening the gate or losing the lifecycle finding. Preserve frozen tests, Astra-high fixes, three Sol-high reviews and nit-only completion. Use compressed CI per attempt and the full matrix only after the sweep. Transition to a reviewed new runtime before #1224. Resume safely, monitor the runs, integrate only verified candidates into integration/bugfixes, and keep the tracking issue current.
