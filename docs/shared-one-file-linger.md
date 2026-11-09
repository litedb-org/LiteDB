# Shared one-file linger

## Design (P5-SHARED-LINGER)

The owner chose default-on linger on 2026-11-09. The intended default is the
shortest measured interval in the 1–2 second range that removes most per-request
cost for a typical burst. Start measurement with 1000 ms; do not call that a
measured default until hosted Linux x64, Windows x64 and macOS ARM64 net8 results
establish it. This design is not an enablement claim.

The process participant moves from active to idle after its last facade and
escaping result release, then to closing when the timer expires, then closed.
An eligible open atomically cancels idle expiry and reuses the participant.
Closing cannot be cancelled: same-file openers must wait for storage and native
admission teardown before constructing another participant. Timer callbacks,
explicit release, process exit and reopen race through one lifetime gate.

Idle retention keeps the participant, authority mapping and native admission.
Retaining an engine is a separate lifetime edge: an idle read cache may survive
only under its existing invalidation protocol; a writable engine cannot be
transferred between facade owners without transferring and validating ownership.
Writer tenure must obey the existing pressure/hold rules, including while idle;
linger must never reserve cross-process WRITE for its full interval. Retaining
only the mapping is an intermediate implementation, not the full performance
objective. Commit acknowledgement still requires a covering sync when durable
commits are enabled. Linger changes no durability promise.

Expose `SharedLinger` in milliseconds on `EngineSettings` and `ConnectionString`
(`shared linger=0` disables it). Apply only to ABI-2 physical-file Shared;
legacy/caller-stream/fallback paths stay unchanged. Read-only participates using
its existing byte-preserving rules. Incompatible settings may evict an idle
participant, but must still reject a mismatch against active facades/results.
Capture settings before publishing the participant; never reuse facade callbacks.

`LiteDatabase.ReleaseIdle(filename)` and `ReleaseAllIdle()` synchronously release
idle participants. Process exit performs the same best-effort cleanup. Release
must run the existing final-close checkpoint, including pending WAL work, before
closing mapping and admission. Preserve uncertain fences and recoverable WAL on
failure. Timer exceptions cannot escape to the runtime. Explicit release must
not silently hide an unexpected teardown failure.

Cross-process exclusive requests must use the mapped wake/request protocol,
with a renewable bounded request, and wait at most TIMEOUT. Direct open,
maintenance/Rebuild, offline upgrade, final close and exclusive DMS conversion
must request release from idle peers before claiming exclusivity. A request is
only a hint to relinquish idle resources; it never grants native admission or
storage access. Active owners/results remain authoritative. Native admission
must not wait for a timer that needs the same process gate; bootstrap/main/DMS
lock order must also allow a releasing peer to finish final-close work. Process
death expires requests and releases OS locks; stale requests must not reserve
access. Existing ABI-2 peers without linger continue to work.

Phase4 handles retain the same ownership that they do today. Neither a live
handle nor a cursor counts as idle. Completion/rollback must precede idle
publication. Same-thread foreign-facade error 141 remains unchanged. Merge into
phase4 only after handle/cursor/disposal interleavings are tested there.

On Windows the file can remain open briefly after Dispose. Before immediate
File.Delete, replacement or external offline work, call ReleaseIdle(filename)
after disposing every owner, or use shared linger=0. Test helpers must do this
explicitly; do not loosen deletion/recovery assertions.

## Qualification gate

Before enabling a nonzero default or merging into phase3: failing-first reuse
(identity/counter), expiry, zero, mismatch, read-only and each release trigger;
real-process Direct/Rebuild handoff, exit and crash/recovery; pending-WAL final
checkpoint; live cursor/handle and 141 tests; request/expiry/reopen forced races.
Run the request harness pinned at 93464f2c1 before/after on all three OSes with
1/4 processes, then full CI, both P1 safety workflows and Shared stress on the
exact phase3 head. Preserve both parents' safety-registry leaves on merges.
Missing safety evidence blocks default enablement and upward integration.
