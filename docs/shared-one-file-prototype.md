# One-file Shared prototype integration contract

ABI 2 is opt-in and dormant by default until slices 5–10 are qualified. The
existing multi-file Shared implementation remains the default. ABI 1/2 deployment
requires all old executables stopped: unmodified old clients cannot be fenced by
a protocol they do not implement. No database-format change or rebuild is needed.

Both modes use NativeAdmission and DatabaseFileLock.Admission on the actual data
file: Direct exclusive, Shared shared, for the full physical participant lifetime.
The path, physical identity, sidecar namespace and native-volume policies are the
Direct base's policies. Arbitrary streams, memory and temp are not pooled.

Order: path creation/replacement rendezvous -> process-entry publication gate
(short; no slow initialization under it) -> native admission -> Shared turnstile
-> main mutex -> WRITE range -> core/storage locks. Snapshot/reader gates must
never be held while waiting for writer ownership. Operations must not reenter the
registry gate. Teardown finishes cores/results, then unmaps and closes slot/DMS,
then releases data admission. A result retains actual mapping/admission resources.
Direct's existing global cold-open gates remain a separately reported limitation;
this prototype adds no work on Direct operations.

Each facade carries a unique reference-identity TransactionOwnerToken, propagated
explicitly through EngineSettings to every reopened core and monitor. It contains
no Thread, AsyncLocal or execution context. Legacy APIs still use actual Thread
identity for transaction lookup/completion. Future handles can carry tokens across
threads; this seam does not authorize current legacy calls to move threads or
implement Shared's unified writer ownership (slice 7).

SharedMemoryLayout fixes the bootstrap and lock offsets. Configurable block stride,
capacities and map size are declared in two checksummed envelopes. Locks are beyond
the maximum payload at 1 GiB so Windows range locks never intersect mapped data.
Process requests and wake words have separate cache lines. A reserved 512-byte
coordinator region has no semantics yet; no coordinator is implemented. A newcomer
must eventually claim/publish/wake without the writer mutex (slice 8); slot
publication therefore needs independent bootstrap serialization, not writer tenure.
