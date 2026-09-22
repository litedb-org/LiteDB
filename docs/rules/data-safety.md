# Data safety

These are primary design and acceptance requirements for implementation work.
They cover direct storage changes and indirect effects through queries, mapping,
serialization, concurrency, and resource ownership. They take priority over
performance or implementation convenience. They are requirements to verify, not
a claim that every existing path already satisfies them.

## Database safety is a completion gate

An implementation task cannot be declared complete while its database-safety
impact remains unproven by tests. Review and passing unrelated tests are not
sufficient. Identify the affected invariants, failure paths, and interactions,
then supply passing automated evidence that the change preserves them.

- Cover the intended behavior and adverse effects: lost/duplicated records,
  incorrect reads or index results, broken rollback/recovery, incompatible legacy
  files, leaked ownership, and unintended file mutation where applicable.
- Increase test breadth and depth with complexity. Additional branches, persistent
  states, concurrency, and shared callers require additional boundary, interaction,
  failure, and regression cases. A fixed test count or coverage percentage is not
  a safety argument; simplify code when its state space cannot be validated adequately.
- Changes to database/WAL/backup/temp-file handling or caller-stream I/O always
  require targeted tests. Include failures and recovery at the affected boundaries,
  real file-backed reopen checks, and verification that unrelated data is preserved.
  In-memory happy-path tests alone cannot satisfy this gate.
- For each identified safety risk, record the invariant, discriminating test,
  tested revision/configuration, and result. Run the relevant broader regression
  suites as well as new focused tests. Fix discovered failures and close coverage
  gaps before reporting completion; do not weaken assertions or defer blockers
  merely to finish the task.
- State the tested fault model and platform/device assumptions. Finite tests do
  not prove safety under every imaginable fault. Missing evidence, an unexplained
  failure, or an unresolved safety concern means the task remains incomplete;
  never replace that evidence with an assertion of absolute certainty.

## Preserve legacy databases without unnecessary rebuilding

A database-version upgrade should not force a full rebuild of a legacy database
unless correctness makes it unavoidable. Prefer compatible reads, small metadata
transitions, or safe incremental conversion. Mixed old/new representations may
coexist when their interpretation and index semantics remain unambiguous.

If rebuilding is necessary, explain the incompatibility and why a less invasive
transition cannot work. Rebuild only the affected structures where possible.
Document startup cost, temporary space, read-only behavior, and recovery before
requiring conversion. A version bump alone is not justification for a rebuild.

## Make upgrades atomic and repeatable

An upgrade must be safe to retry after interruption and a no-op after completion.
At each durable boundary, reopening must expose a consistent old or new state,
or recover/resume the transition before admitting ordinary operations. It must
never expose a partially converted state as a successfully upgraded database.

Atomicity is a property of the complete recovery protocol, not an assumption that
a header write or filesystem operation cannot tear. Order data, WAL, metadata,
version publication, flushes, and backup cleanup accordingly. Keep enough durable
recovery information until the new state is validated and committed. Recovery must
itself tolerate interruption, including repeated power loss at the same step.

## The engine must not corrupt its own database

Valid operations, concurrency, cancellation, exceptions, disposal, process death,
and restart must preserve storage invariants and committed data under the documented
durability contract. Never turn an operation failure into silent data loss or let
later writes build on a state whose consistency is uncertain.

Account explicitly for disk-full conditions, failed reads/writes/flushes, short and
torn writes, lost or reordered writes, and damaged stored bytes. If safe continuation
cannot be established, stop affected writes, surface the failure, and preserve the
data/WAL/backups needed for recovery. Do not acknowledge a durability guarantee that
the underlying operation failed to establish. State required filesystem/device
guarantees and distinguish detected damage from recoverable damage.

## Corruption must be discoverable and visible

Provide integrity validation for affected on-disk structures and validate critical
metadata before trusting it. Detection should cover damaged bytes where integrity
information is available, as well as broken page ownership, links, lengths, index
ordering, and document/index inconsistencies. A successful open or checksum alone
does not prove logical consistency.

Detected corruption must produce an actionable diagnostic: identify the affected
file/structure and page or offset when known, the failed invariant, and whether
access or recovery can continue safely. Never silently treat corrupt data as empty,
missing, or successfully repaired. Unreliable reads/writes must fail clearly;
warnings are appropriate only when continuing is known to be safe.

Keep detection separate from destructive repair. Preserve recovery evidence and
report any unrecoverable records or salvage loss explicitly. Integrity inspection
should not modify the database merely to diagnose it.

## Required evidence

Storage changes need targeted fault-injection and restart tests, not only graceful
close/reopen tests. Interrupt each affected persistent transition; retry recovery
and upgrade repeatedly; verify completed upgrades remain unchanged on another open.
Compare recovered state with an independent committed-state model, including indexes
and sidecars. Inject corruption and verify it is detected and reported without
silently accepting, discarding, or rewriting the damaged state.

See [compatibility](compatibility.md) for migration boundaries,
[storage ownership](storage-ownership.md) for lifetime and replacement invariants,
and [validation](validation.md) for the existing fault models and fuzz targets.
