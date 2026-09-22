# Storage PR integration order

The dependency order is `dev` → #2998 checksums → #2924 index ordering →
#2999 compact storage → #3000 snapshot-aware checkpoint/reclamation.
#2978 rebuild recovery is already in dev and is included throughout.

| PR | Format | Base while stacked |
| --- | --- | --- |
| #2998 | v10 | dev |
| #2924 | v11 | codex/stack-checksums (#2998) |
| #2999 | v12 | codex/pr2908-index-compatibility (#2924) |
| #3000 | lazy v13 retirement | codex/stack-compact (#2999) |

Merge each completed PR separately, in that order. After a parent squash merge,
merge dev into its child, retarget that child to dev, check the resulting diff,
and rerun its checks. Keep upstream mirror branches until their children no longer
need them. Different PRs must not assign the same version to incompatible layouts.

Checksum conversion is header-only after legacy WAL recovery. Index migration
additionally validates and rewrites affected indexes atomically. Compact storage
changes new writes only; an explicit rebuild can rewrite existing documents.
BSON-only creation/rebuild retains v11 checksums and index ordering, so `Legacy`
is an encoding policy rather than a downgrade to released v8/v9 engines.

## MVCC transaction-proof boundary

The original #2936 protocol clears obsolete committed frames and reuses their
physical slots. The v10 checksum protocol verifies every transaction's frame
count/digest and an uninterrupted confirmation sequence. Clearing even an
unreachable frame invalidates that proof; clearing an entire obsolete transaction
leaves a confirmation-sequence gap. Existing checksum regression tests
`StaleReusedSlotsWithinGeneration_FailTransactionDigest`,
`ConfirmationCount_IsValidatedEvenWhenItsFrameChecksumIsValid`, and
`LostConfirmation_CannotAllowLaterCommitsThroughASequenceGap` exercise these gates.

A mechanical merge, accepting zero pages, or disabling digest validation is unsafe.
#3000 implements durable v13 witnesses to distinguish
intentionally retired frames from missing/corrupt frames. Snapshot versions remain
stable across reuse/recovery, with a version boundary before retirement records
are written. Its implementation and final-head validation require separate review.
Partial checkpoint must preserve the header journal's WAL binding until its data
writes are durable. Recovery and rebuild must use the same verifier.

Required evidence includes old/new readers, plain/encrypted files, interrupted
reclamation publication, torn/reused slots, repeated recovery, live readers paused
after resolving an offset, and exact document/secondary-index models. Earlier tests
and storage-savings measurements for the legacy protocol do not certify the integrated v13 protocol. #2923 ownership/identity remains outside this stack for separate review.
