# Session review behind these rules

This is an audit note, not additional agent instructions. Reviewed on 2026-09-22
against checkout `e4ce3b8992abaa7fd2f25256afd03a9cbb00a769`.

## Discovery and limits

The first pass read only the first JSONL record (`session_meta`) from files in
`~/.codex/sessions` and `~/.codex/archived_sessions`. It matched LiteDB checkout
paths or repository metadata before reading conversation bodies. Discovery scanned
1,933 files (about 3.7 GB total) in about 0.25 seconds and selected 1,295 files
(about 2.0 GB) across 77 recorded checkout paths. This includes the main checkout,
current worktrees such as `f0d4/LiteDB`, removed worktrees, and archived tasks.

At discovery time there were 1,293 unique task IDs; the local thread database
independently contained the same 1,293 LiteDB IDs, with none missing. The selected files
comprised 86 root-session records and 1,209 child-agent records. Duplicate files
and inherited messages are not independent evidence of repeated user corrections.
The current cleanup task and the three reviewers subsequently created for it were
excluded as sources of historical lessons.

All selected files were processed to extract user messages, final responses, and
available compaction summaries. Injected instructions, automatic goal continuations,
tool output, and repeated text were excluded from the main reading set. Regex
filters surfaced correction language, findings, ownership/compatibility failures,
and test gaps. Detailed review concentrated on recurring corrections and technical
findings, rather than rereading every tool invocation in the corpus.

Three GPT-5.6 Luna reviewers independently checked workflow, storage/validation,
and query/mapping lessons. TypeSafe's installed skill and live API guidance were
used to rank 38 shortlisted conversations for reusable workflow and technical
lessons; those judgments prioritized inspection and were not treated as proof.
Source messages and current repository code/docs determined the retained rules.

## Representative evidence

Task IDs locate the source JSONL files by filename. `U` denotes a source user
message's JSONL line number. These examples explain the rules; they do not retain
old merge approvals, issue status, or branch-specific implementation claims.

| Lesson | Source task / evidence | Destination |
| --- | --- | --- |
| Stop force-pushing during iteration | `01a0bfd0-d381-7e12-87f1-fab560e48a94` U1014; `01a0bed6-7d74-74b1-87db-a2a6b97c081c` U2349 | [Workflow](workflow.md) |
| Continue useful work during CI; verify after the final push | `01a0c0bb-87f5-7582-879a-37e642dcc164` U578/U736; `01a0ba79-4728-77b2-b63a-eb95fad27d2b` U487 | [Workflow](workflow.md) |
| Separate independently revertible bug fixes from infrastructure | `01a0bfd0-d381-7e12-87f1-fab560e48a94` U10407/U10446; `01a0b1d7-0472-7430-96f4-e03446acb61d` U1253 | [Workflow](workflow.md) |
| Read all review surfaces and phrase blockers collaboratively | `01a0c45c-05d1-7d40-9314-83e6d4790b2f` U9/U78; PR-remediation tasks including `01a09c8d-d71b-7ea0-a283-483d039896a4` | [Workflow](workflow.md) |
| Historical reproduction, independent controls, and honest status | `01a09f8b-1d66-7f22-8bc4-b4310c0d0567` U434/U1054/U1068; child review `01a09db9-4e98-7ff2-8679-930e361ddb4e` | [Validation](validation.md) |
| Wider fuzz grammar, independent CLR oracle, non-UTC runs | `01a0bed6-7d74-74b1-87db-a2a6b97c081c` U394/U670 | [Validation](validation.md), [queries](query-expressions.md) |
| Preserve concurrent coverage when fixing a flaky test | `01a0c543-ee00-79b3-b44e-e5e32fae46c6` U267 and subsequent explanation | [Validation](validation.md) |
| Test-only instrumentation must have no production cost | `01a0bfd0-d381-7e12-87f1-fab560e48a94` U5010 | [Development](development.md) |
| Explain migration churn, mixed states, crash resumption, and version conflicts | `01a0c095-d486-7340-acc7-84c6ff0c6df8` U3559/U4615/U4651; `01a0c4e5-d029-7821-9149-118ed2a3a87b` U1957–U2024 | [Compatibility](compatibility.md) |
| Restore a complete data/WAL state, not just the data file | `01a0c451-2a31-7190-9399-c2fad327e0ae` U373/U981; child review `01a0ab10-90f7-7d12-86a6-26e28e333cd6` | [Storage ownership](storage-ownership.md) |
| Retain cursor snapshots and actual thread identity | `01a0c7e9-6e00-7390-8d48-0d1818c36e6c`; child reviews `01a0ac3b-b19d-7543-beae-70db9a5592cc`, `01a0ac3b-89e7-7d61-ba76-30a048b97c8d` | [Storage ownership](storage-ownership.md) |
| Preserve declared dictionary contracts and mapper extension points | Child reviews `01a0abf6-ccf6-7652-b041-271d19309a22`, `01a0ac06-3222-77e2-86df-35dff8608a69`, `01a0ac2d-da9d-74b0-b5bc-ec175df380c5` | [Mapping](mapping-serialization.md) |
| Keep query-helper text round trips and mutable parameter independence | `01a0a9be-5b3f-7810-9f08-a77ba87c88ce` U26825; child reviews `01a0af09-2dec-7812-b07b-412052260d2e`, `01a0af09-075d-7bc3-a906-df6648c8ed08` | [Queries](query-expressions.md) |
| Distinguish automatic speedups, opt-in reuse, WAL progress, reuse, and shrinkage | `01a0ac1a-24d4-7fb1-a6fa-4eb53fab1b46` U503–U572; `01a0b6fe-cdfa-7b52-8844-4aeeb00a64dc` U415/U427/U437 | [Performance](performance.md) |
| Structural generator equality and separate location-sensitive diagnostics | `01a0ac7e-d127-7ab3-be92-e85abdf3129e` U1124/U1547/U2774/U2786 | [Code generation](code-generation.md) |

## What was deliberately excluded

One-off merge/admin/release permissions, requested reviewer counts/models, old
test counts, branch names, and historical performance numbers are not standing
rules. The explicit AOT-branch force-push request was an exception for that task.
Checksums, compact storage, index migration, and generator work in other branches
do not establish this checkout's format or feature support. Unverified reviewer
claims were not promoted into assertions that current code has a defect.

Raw transcripts, private attachments, credentials, and the multi-gigabyte corpus
remain outside the repository. The rule files retain reusable guidance and link
existing design/validation docs instead of duplicating the session history.
