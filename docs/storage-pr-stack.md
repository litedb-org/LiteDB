# Storage PR integration order

The dependency order is `dev` → #2954 checksums → #2924 index ordering →
#2937 compact storage → #2936 snapshot-aware checkpoint/reclamation.
#2978 rebuild recovery is already in dev and is included throughout.

| PR | Format | Base while stacked |
| --- | --- | --- |
| #2954 | v10 | dev |
| #2924 | v11 | codex/stack-checksums (upstream mirror of #2954) |
| #2937 | v12 | codex/pr2908-index-compatibility (#2924) |
| #2936 | requires a new reclamation boundary | codex/stack-compact (upstream mirror of #2937) |

Merge each completed PR separately, in that order. After a parent squash merge,
merge dev into its child, retarget that child to dev, check the resulting diff,
and rerun its checks. Keep upstream mirror branches until their children no longer
need them. Different PRs must not assign the same version to incompatible layouts.

Checksum conversion is header-only after legacy WAL recovery. Index migration
additionally validates and rewrites affected indexes atomically. Compact storage
changes new writes only; an explicit rebuild can rewrite existing documents.
BSON-only creation/rebuild retains v11 checksums and index ordering, so `Legacy`
is an encoding policy rather than a downgrade to released v8/v9 engines.

## MVCC integration blocker

The original #2936 protocol clears obsolete committed frames and reuses their
physical slots. The v10 checksum protocol verifies every transaction's frame
count/digest and an uninterrupted confirmation sequence. Clearing even an
unreachable frame invalidates that proof; clearing an entire obsolete transaction
leaves a confirmation-sequence gap. Existing checksum regression tests
`StaleReusedSlotsWithinGeneration_FailTransactionDigest`,
`ConfirmationCount_IsValidatedEvenWhenItsFrameChecksumIsValid`, and
`LostConfirmation_CannotAllowLaterCommitsThroughASequenceGap` exercise these gates.

A mechanical merge, accepting zero pages, or disabling digest validation is unsafe.
#2936 must remain draft until it has a durable reclamation protocol that distinguishes
intentionally retired frames from missing/corrupt frames, stable snapshot versions
across reuse/recovery, and a version boundary before either representation is written.
Partial checkpoint must preserve the header journal's WAL binding until its data
writes are durable. Recovery and rebuild must use the same verifier.

Required evidence includes old/new readers, plain/encrypted files, interrupted
reclamation publication, torn/reused slots, repeated recovery, live readers paused
after resolving an offset, and exact document/secondary-index models. Earlier tests
and storage-savings measurements for the legacy protocol do not certify that future
integration. #2923 ownership/identity remains outside this stack for separate review.
