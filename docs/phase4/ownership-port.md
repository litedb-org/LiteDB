# Ownership substrate (P4-S1-OWNERSHIP)

This ports litedb-org/LiteDB#3114 at ce8636f6d17875de1a6122c73f503d79237bb541
(the mechanism commit is 94d5f0274) onto Phase 3 at 9fd852a4a.
It exposes no public handles. Context dispatch, pin handoff with retained holds,
close/reaper integration and public binding remain later deliverables.

TransactionLease carries facade identity, a distinct token, generation, kind and
an actual Thread only for legacy ownership. Legacy admission/collection recursion
continues using actual Thread identity; handle leases use reference identity.
Monitor clones retain their shared legacy slot and independent facade tokens.
Internal handle creation lives outside that slot; exact lease release cannot erase
an unrelated legacy transaction. Q1 preserves error 141 for a same-thread foreign
legacy owner. Q2 reserves Shared writer ownership even for read-only handles;
the local-owner substrate supports that reservation without treating creator death
as abandonment. Public Shared integration remains disabled until step 5.

The existing participant token, writer tenure budget and native holder thread stay
in place. No new participant, child core or mutex holder pool is introduced.

Lock ordering: path admission precedes Direct lifecycle/registry admission.
Cold opening reserves the physical entry under the registry gate, constructs the
core outside that gate (including recovery/upgrade replacement opens), then
publishes it and wakes peers. Failed construction releases its admission and
reservation before peers retry; same-file peers cannot construct a second core.
Maintenance retains lifecycle serialization while waiting on transaction admission;
the registry gate is released first. Transaction admission precedes WAL transaction
ID allocation. Collection snapshots precede header/WAL publication. Existing header
then WAL-writer ordering remains intact. Group completion releases snapshots and
header/publication/WAL monitors before waiting for its commit ticket; admission
is released only after transaction cleanup. Assertions execute in DEBUG or TESTING.

## Source test disposition

| Source at ce8636f6d | Destination | Obligation |
| --- | --- | --- |
| RuntimeLock_Tests.CollectionLock_Times_Out_While_Held_And_Can_Be_Reentered_After_Release | Same test | Port explicit owners; retain contention/timeout/reacquisition assertions |
| RuntimeLock_Tests.MemoryCache_Extends_Once_Per_Concurrent_Request | Same test, unchanged | Preserve concurrent cache ownership assertions |
| Existing transaction-gate regressions | Issue2991_Tests and Issue2814Admission_Tests, unchanged | Foreign cursor release, maintenance admission and ID allocation ordering |
| New | OwnershipLease_Tests | Cross-thread recursion/release, wrong owner, final release wakeup, peer admission isolation, legacy 141, creator exit and generation fencing |

Hosted evidence is recorded in the step log, not inferred from historical source
runs. The WAL handle safety workflow includes the new suite and resolves all
existing Direct host, same-thread, group-commit, SharedOneFile, SharedWriterTenure,
cursor and cleanup suites through broad class-prefix filters. Standard CI's
complete-suite partition verifier also discovers these tests.
