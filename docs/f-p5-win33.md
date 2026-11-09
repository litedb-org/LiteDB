# Windows cold admission after requester death

Classification: test-harness timing assumption (b). `DatabaseFileLock.Lock`
intentionally uses `LOCKFILE_FAIL_IMMEDIATELY`; native admission distinguishes
incompatible Direct/Shared lifetimes, rather than waiting for transaction ownership.
See [native admission](direct-process-host.md#native-admission) and the explicitly
retained fail-fast policy in [DMS contention](f-p5-macos-eagain.md).
The native admission implementation already exists on phase1. No product change
is proposed here; a change to that policy would require separate orchestrator approval.

The tenure fixture kills a suspended requester with `sharedFilesRemainOpen: true`
while the parent still participates. That probes child-private reader leases only.
It then closes the parent and immediately opens Direct. Neither process exit nor
SafeHandle disposal establishes that the kernel has released the admission byte.
[Microsoft documents resource-dependent delayed release](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex).
The actual kernel holder in the historical failure remains unknown.

After all intentional participants have closed, the test now waits at most five
seconds to acquire the exact admission byte exclusively through a separate native
handle. It explicitly unlocks the probe before opening Direct. It never opens,
creates, truncates or recovers storage as part of the wait; errors other than
actual contention propagate. The two original cold recovery passes and every
acknowledged-data, index, sentinel and tenure assertion are retained. A persistent
lock still fails the test. Focused tests cover persistent ownership, delayed release
and unchanged bytes. This is a lifecycle precondition, not a retry of failed recovery.

The scratch-only workflow runs Windows 2022/2025, x64, .NET 8/10. The original
fixture receives the same observational graft as diagnostic run 42685168001,
plus a precise admission-range release probe timed from the original failure.
Original failures are rethrown and retained in TRX; before results are research
outcomes, never qualification. Fixed tests execute directly from the pushed head,
without a graft. Fifty sessions per matrix cell retain both requester variants;
the first additionally exercises all tenure scenarios and release helper controls.
Artifacts retain source SHA, actual runtime, build/session logs, TRX and observations.

Historical evidence: diagnostic run 42685168001 attempts 1/2 reproduced native33
on candidate23a5c6c7b and baselinea79db8307, respectively. All five tracked parent
handles were closed. The first retry verified all acknowledged rows/indexes,
sentinel and tenure902 in132/105ms including verification. Those times are upper
bounds including recovery, not measurements of kernel unlock latency.

Hosted results for this change are pending; no completed safety qualification is claimed.
