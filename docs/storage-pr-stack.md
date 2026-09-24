# Storage PR integration order

Native GitHub stack #3001 targets `dev`:

| PR | Format | Base |
| --- | --- | --- |
| #2998 checksums (replaces #2954) | v10 | dev |
| #2924 index ordering | v11 | codex/stack-checksums |
| #2999 compact storage (replaces #2937) | v12 | codex/pr2908-index-compatibility |
| #3000 MVCC (replaces #2936) | lazy v13 retirement | codex/stack-compact |

The original branches and reviews are preserved; frozen heads also live under
`codex/backup/storage-stack-20260922/pr-<original-number>`. #2978 rebuild recovery
is included throughout. #2923 remains outside this stack for separate review.

Merge only reviewed, validated layers, in dependency order. Stack registration
is not merge-readiness evidence. After a parent lands, integrate the new trunk,
check the child's resulting diff and revalidate it. Keep dependent branches until
their children no longer need them; do not assign one version to incompatible layouts.

Use the [storage stack safety acceptance map](storage-stack-safety.md) to connect
each layer's persistent invariants to regression suites, fuzz targets and actual
predecessor compatibility probes, with the fault-model and operational limits.

Checksum migration changes only the header after legacy WAL recovery; old pages
receive checksums lazily. Index migration separately validates and rewrites affected
indexes atomically. Compact storage changes new writes. MVCC adds durable retirement
witnesses and promotes only when reclamation first needs them; it does not rebuild
existing documents. See [snapshot checkpointing](mvcc-checkpoint.md) and
[the retirement format](mvcc-retirement-format.md) for ordering and failure cases.

The original MVCC implementation could not simply clear checksummed frames:
transaction counts/digests and contiguous confirmation sequence would be lost.
The v13 witnesses preserve those proofs. Keep the existing missing-frame,
count/digest and sequence-gap regression tests alongside the retirement cases.
Approval requires crash, corruption, repeated recovery, live-reader and separate-
process evidence against full document and secondary-index models on the final head.
