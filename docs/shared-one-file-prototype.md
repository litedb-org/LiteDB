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

Through slice 3 all read-only engines still open under the named mutex; ABI-1
cached admission is bypassed. Exact mapped records replace only the reader
provider. Unknown liveness/records skip checkpoint and reclamation. Generation
exhaustion throws before query/callback execution. Rebuild, file-replacing upgrade
and auto-rebuild are explicitly refused until slice 9. Existing artifacts are
preserved/refused; offline cleanup is not implemented in this round.

The first exclusive-DMS initializer owns WRITE while invoking protected storage
validation. No live ABI-2 writer can exist in that state. Live attachment releases
the independent bootstrap gate after publishing its slot/request and before
waiting for main ownership. Ordinary writer main->WRITE publication belongs to
slice 5; no warm status is trusted by these slices.

Through slice 4 the registry shares one mapping, DMS, process-slot lock, exact
reader table, durability diagnostics and checkpoint backoff across compatible
local facades. Main ownership/core state remain per facade until slices 7/8.
The participant captures no facade callbacks or facade token. Initialization is
per physical identity; unrelated identities never wait behind its entry gate.
Closing the last facade/result retires that exact entry without erasing a successor.

Qualification is currently Linux x64/.NET 8+ on the mapped local-volume allowlist.
Other mapped platforms explicitly refuse opt-in; their prototype tests report a
capability skip, while native range tests remain enabled. Windows/Darwin/ARM and
Framework mapped qualification are outstanding. Defaults retain ABI 1. The map
starts with committed-version unknown and fast-admission disabled; slices 5/6 must
publish all storage fences before any warm reader trusts these reserved fields.
