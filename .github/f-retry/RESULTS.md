# F-RETRY paired diagnosis

Captured failure classification: **(b), advancing work with durable-I/O tail latency**.
The sampled child was inside `FlushFileBuffers`, reached through
`ReclaimLogPages -> TryCloseCheckpoint -> LiteEngine.Close -> SharedEngine.CloseDatabase -> Insert`.
It completed four more inserts after the five-second assertion deadline.

Hosted run: https://github.com/litedb-org/LiteDB/actions/runs/42244689681
Diagnostic revision: `50076fc27ad9986cff96f337378680a0ea4f3b44`.
Source pins: phase2 `ef2e0762703e2d936ac26b49534f998e39a563b7`,
dev `b3c4406231908c6ca29f6d4b2769aa08351d6fc2`.

The workflow alternates both revisions on the same runner, using ordinary xUnit
execution of only the four child-process theory cases, and stops a platform after
its first failing pair. The original five-second deadline and runsettings remain
unchanged. Runtime/architecture are verified in the child, with an explicit test
host platform. Windows x86 used .NET 8.0.33.

| Platform | Paired iterations | Phase2 | Dev |
| --- | ---: | --- | --- |
| Windows x86 | 50 | 199 passed, 1 timeout | 200 passed |
| Ubuntu x64 | 300 | 1200 passed | 1200 passed |
| Windows x64 | pending | pending | pending |

## Failure evidence

Artifact `f-retry-windows-latest-x86/phase2/49` contains the timeout, both managed
stack reports (each tool exited 0), and child stderr. The case was
`encrypted: False, pinned: True`, child PID 4528 and parent PID 1368.

| Event (UTC) | Timestamp |
| --- | --- |
| Insert 3000 begins | 15:46:15.4242033 |
| Insert 3007 completes | 15:46:18.3614485 |
| Original deadline fails | 15:46:20.4140333 |
| Insert 3008 completes | 15:46:20.9501706 |
| Insert 3009 completes | 15:46:23.7563748 |
| Insert 3010 completes | 15:46:25.1837767 |
| Insert 3011 completes | 15:46:27.1197238 |

The child's main thread is doing a durable WAL-reclamation flush. Its mutex
helper awaits commands. The parent test awaits the failed read and diagnostic
collection. This sample shows continued progress rather than a persistent lost
wake-up or leaked writer hold. Post-deadline timings can include observer effects
from stack collection and are excluded from ordinary latency summaries.

## Encrypted insert latency

Milliseconds, phase2 / dev. Exclude insert 3000 (cold/JIT cost) and post-timeout
samples; quantiles describe individual insert completions. Each Windows x86 route
has 950 samples per revision; each Ubuntu route has 5700.

| Platform | Pinned | Median | p95 |
| --- | --- | --- | --- |
| Windows x86 | false | 9.908 / 9.7595 | 42.377 / 38.766 |
| Windows x86 | true | 11.3865 / 11.174 | 74.327 / 70.956 |
| Ubuntu x64 | false | 8.7735 / 8.828 | 22.571 / 22.196 |
| Ubuntu x64 | true | 9.681 / 9.668 | 30.717 / 29.719 |

The x86 encrypted median difference is +1.5–1.9%; Ubuntu is -0.6% to +0.1%.
The captured timeout does not require encryption. Its observed cost centre is
repeated core close/checkpoint and synchronous durable WAL flushing.
`SharedOneFile` is false here, so the ABI-2 mapped writer is inactive.
`ReclaimLogPages`, its `SyncLogBarrier` call and the actual flush implementation
have no source delta between these pins; encryption also uses the same AES stream. Encrypted stream opens rederive AES keys
through `Rfc2898DeriveBytes`; repeated core opens pay that existing cost in both
revisions. No attribution of the small revision difference to that unchanged
code is established.
These data do not establish a substantial encryption-specific Phase2 regression
or identify an introduced durable fence. Attribution of small latency differences
remains unresolved; hosted volume/scheduling variability is a plausible source of
the transient I/O tail, not a proven cause.

No product fix or deadline change follows from this classification. The original
Linux timeout had no valid stack capture, so the sampled Windows failure does not
prove the cause of every historical timeout or exclude a separate rarer defect.

## Reproduce the analysis

```sh
gh run download 42244689681 -R litedb-org/LiteDB -D artifacts_temp/f-retry
python .github/f-retry/analyze.py artifacts_temp/f-retry
```

The files under `.github/f-retry` supply the identical diagnostic overlay for both
pinned trees. The extra commit containing this report and analysis script does not
change the binaries exercised by the linked diagnostic run. This was a targeted
diagnostic run, not the full suite or a production safety qualification.
