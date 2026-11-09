# Direct/default-mode regression research

Research only; no production fix or safety qualification. All measurements run on hosted GitHub runners. Adjacent phase boundaries and narrowing probes use the existing benchmark gate comparison mechanism: same-job before/after binaries, six interleaved rounds, two independent jobs per boundary/platform/runtime. Thresholds are unchanged. Subsets are report-only. Historical A/A bounds are advisory, not a new same-job A/A run; raw confidence bounds remain in gate.json and comparisons.json.

## Fixed stack

|Phase|Measured head|Merge base with next phase|
|---|---|---|
|1|a8727ffce5d11d8fb74a87c3dd6d2ce8e3d7def3|same as head|
|2|457cec42502e8d21c85c400ac35de447a56ebe9e|same as head|
|3|b70b79ec274c981a340da1af54104483d96fe351|same as head|
|4|e507faad40e968aa2fcf63ae6dd01fd7def2e07b|endpoint|

These heads were resolved with git ls-remote before dispatch; later branch movement does not change the measured revisions. Full per-platform/config tables and raw identities are linked from the step log.

## Open/close: Phase3, 32a0ca52d4c7ac42764ec917a46a2a723a80cef5

Windows Phase3/Phase2 default ratios: net8 1.1950/1.2064, net10 1.2009/1.1078; cache net8 1.2306/1.2303, net10 1.1921/1.1653. Linux net10 default 1.1146/1.0480 and cache 1.0982/1.0698. Phase4 Windows open is largely flat. The introducing canonicalization commit alone gives Windows net10 default 1.1525/1.1673, cache 1.1911/1.1657; Linux net10 default 1.0982/1.0391, cache 1.0961/1.1025. Windows default historical A/A upper is unusually loose (1.3657); do not call that specific row beyond its envelope. Windows net8 and net10 cache, plus Linux net10, supply stronger repeated discrimination.

32a adds DatabaseFileIdentity.CanonicalPath inside RebuildRecovery.EnsureAvailable. Cold Direct admission calls this guard four times (NativeAdmission.Normalize, pre-open admission, post-Admit, LiteEngine.Open), in addition to existing normalized-path resolution. On Windows each canonical resolution creates a 32,768-character StringBuilder and resolves the file via CreateFile/GetFinalPathName. The commit adds 5,309,440 bytes per 20 opens, virtually all the Phase3 increase (5,309,760 bytes). Linux adds 12,160 bytes instead. Thus registry lookup alone does not explain the platform-specific allocation signature.

Corrected EventPipe profiles corroborate this exact path: CanonicalPath inclusive sampled-thread-time share Windows 2.205% ->10.392%, Linux .827% ->3.888%; EnsureAvailable Windows 1.466% ->10.157%, Linux 1.140% ->3.858%. Profiles include setup and other threads. These are diagnostic stack shares, not CPU utilization or measured optimization gains.

Candidate: retain every marker check at its existing admission/recovery boundary and reuse a scoped canonical identity proven by that same open. Standalone EnsureAvailable must still canonicalize. No global path cache across opens or file replacement. Fix belongs on v6/phase3 and propagates to v6/phase4. Estimated Windows elapsed gain 12–18%, unmeasured; Linux smaller. Required safety tests are in fix-candidates.md.

## Eight durable writers: Phase4, 5cb886e91aea4548db583a51acccf9ff930d4871 and ac972bd2d6e509080b46a1495ba5fa43c4bb719c

Phase4/Phase3 net8 eight-writer ratios 1.2394/1.2885, while one-writer is 1.0242/.9977. Net10 eight-writer is mixed 1.0085/1.2524, with one-writer .9598/.9729. Phase2 and Phase3 do not show a repeated writer slowdown. This localizes the repeated net8 regression to Phase4; it also preserves the new net10 timing variation instead of treating the original 3/3 endpoint finding as universal.

5cb replaces a native lock held across collection ownership (Monitor.TryEnter on net8; System.Threading.Lock.TryEnter on NET9+) with a short monitor-protected logical owner/depth and explicit Monitor.Wait/PulseAll for ownership handoff. Native adaptive contention behavior is replaced by logical-owner parking. It also adds a legacy lease/token allocation and generation counter per transaction. Commit probes net8 eight-writer 1.0670/1.0642 vs one-writer .9907/.9803; net10 eight-writer 1.3822/1.0469 vs one-writer .9785/1.0260. Lease allocations add about 480KB per 5k operations in the one-writer control without a repeated one-writer timing penalty. The net8 substrate profile shows writer-worker Monitor.Wait share18.820% ->37.405%, collection-lock TryEnter16.225% after the rewrite, and durable-prefix wait61.776% ->53.738%. Native pre-change TryEnter can inline, so an absent CollectionLock frame is not zero acquisition cost. The stronger contention response makes owner handoff/parking the leading mechanism, supported by the substrate, FIFO and integrated endpoint profiles.

ac adds FIFO request nodes and cooperative deadlines; it broadcasts when leaving the acquisition try/finally even after installing an owner, then broadcasts again on final release. Commit probes net8 1.0151/1.0949, net10 1.1328/1.0255. These variable increments do not establish FIFO alone as the cause of the full endpoint regression. Offline attribution to writer-worker stacks (excluding seed/verification) in the net10 FIFO profile shows collection lock entry36.380% ->53.973%, Monitor.Wait18.764% ->67.373%, and durable-prefix waiting33.230% ->20.571%. The separate net8 FIFO pair agrees: collection entry43.733% ->56.876%, Monitor.Wait60.990% ->70.543%, durable-prefix26.468% ->18.270%. Each pair uses its own denominator; do not compare absolute weights across hosts. This supports a shift toward owner parking. Inclusive wrapper percentages are not wrapper overhead; thread-time shares are not CPU percentages or fix timings. Ratios from different hosts must not be multiplied or summed. Original release already checks transaction read ownership under a monitor; the rewritten ownership contract is not simply a newly enabled release lock-order assertion. Shared writer-tenure machinery is not activated by these ordinary Direct operations.

Integrated Phase3 ->Phase4 net8 profile corroborates the endpoint: worker Monitor.Wait16.424% ->45.956%, after CollectionLock.TryEnter31.928%, after TransactionWaitBudget.Wait42.415%, durable-prefix wait55.041% ->43.757%. Aggregate thread-time totals are not elapsed ratios. The actual endpoint therefore exhibits the same ownership parking mechanism as the flagged commit paths, though no profile isolates a measured fix gain.

Candidate: efficient owner handoff/parking with bounded adaptive spinning and/or successor notification; evaluate suppressing the acquisition-success broadcast while retaining timeout/head-removal wakeups. Preserve logical handle identity, mixed legacy/handle progress, cancellation/deadlines, self-wait refusal and release-before-durable-ticket ordering. Do not restore thread-owned locks globally or remove fairness without proof. Fix belongs on v6/phase4. Estimated 10–20% less net8 eight-writer elapsed time is an optimization target, not a measured result; net10 gains are unresolved and host/runtime-sensitive.

## Scan: Phase4, activation source 0b4ca2c625f9dcb7b6018224dbfc1f74f042b5c4

Windows net10 Phase4/Phase3 default ratios 1.0418/1.0318, cache 1.0202/1.0215; +38,401,080 allocated bytes per 300k documents. Phase3 net10 default itself is mixed .9994/1.0480 but has zero allocation delta. Windows net8 Phase4 default is 1.0540/1.0169 and cache 1.0519/1.0425. Retain these variations.

Reader type addition 1b5241484 is a negative control: net10 scan .9769/1.0093, zero allocation delta. Actual activation 0b wraps the ordinary LiteDatabase engine with UnboundEngine and its Reader. Each Read and Current builds a Func and masks/restores thread-local TransactionContext current/ticket. Two approximately 64-byte delegates per document explain ~128 bytes/doc and the observed 38.4MB delta; masking is also done when ordinary calls have no active handle.

Exact 0b production fails compilation (missing ReadOnlyRefusalException). Real hosted failure job128926452188 is retained. Next commit02c06a048 removes that invalid reference; UnboundEngine.cs is byte-identical between them. The measured replacement range is a19280cd2 (0b parent) ->02c06a0482448fe4233cc76a1050ee6da19085d9. It bounds the introducing measurement to 0b..02c while source activation is 0b. No historical product patch was applied. Range timings and diagnostic pair are pending.

Candidate: allocation-free value-type binding-mask scope, restoring both context fields in finally on every reader operation including Current, indexer and Dispose. Never simply return the underlying reader or skip masking globally: nested mapper/input callbacks can enter an ordinary facade while a handle executes. Fix belongs on v6/phase4; estimated 3–6% scan elapsed reduction and about 38.4MB fewer allocations/300k documents, unmeasured.

## Evidence status and limits

24 adjacent-phase timing legs and14 introducing/control legs are complete (140 rows). Two buildable reader-range timing legs and the reader diagnostic pair remains queued; two canonical, two FIFO, one substrate and one integrated endpoint diagnostic pairs are complete. All Linux work is complete. The optional residual raw-drain profile fails a separate historical production-build defect (missing LiteEngine.GetMonitor, job128926452490); no primary attribution uses it and the speculative residual open-state fix was removed. Old runs failed only after valid timing evaluation because their trace CLI option was obsolete; those workflows are reported as failed, never green. The corrected exact0b diagnostic leg failed its historical compile defect and is superseded by the buildable range. Neither historical compile defect is called a flake or a passing product result. Raw failed-job logs, exact binary hashes and identities are retained. Profiles use dotnet-trace10.0.750501 sampled-thread-time/common/GC providers; timing and profiling are separate processes.

Ranked fixes and mandatory safety coverage are in fix-candidates.md. No product tests or fixed-binary A/B exist because this step deliberately implements no product fixes. Product safety remains a requirement for the implementation step, not a claimed result of these diagnostic benchmarks.
