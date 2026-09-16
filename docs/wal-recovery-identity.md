# WAL identity and checkpoint generations

Keep a database and its matching WAL together when copying or restoring an open
database. New transactional WALs carry the database's random identity and its
checkpoint generation. A different database or incompatible generation is
rejected with `LiteException.INVALID_WAL` before replay or alignment repair;
the engine preserves both files for recovery. Automatic rebuild applies the
same validation rather than importing a mismatched WAL.

The metadata occupies reserved header bytes. Ordinary databases remain on
format v8; vector databases remain on v9. This does not require a rebuild or an
`Upgrade` option. Before a new WAL can contain a commit, the matching identity
is durably present in the data header. An unconfirmed header at the start of the
WAL carries its identity without becoming a transaction's committed page.

Checkpoint writes and durably flushes the committed data, durably flushes the
WAL (including any unconfirmed tail), then durably
records a new generation, its predecessor and a 128-bit SHA-256 fingerprint of
the completed WAL, then truncates and flushes the WAL. Recovery replays the
current generation, ignores an exact already-checkpointed predecessor, and
rejects every other generation or changed predecessor. This includes later
commits appended by an older writer and restored subsets of a completed WAL.
A read-only open ignores a
completed WAL without truncating or deleting it. The checksum detects damaged
identity metadata; it is not an authentication mechanism.

A torn first-prefix write can therefore require manual recovery even if the
data file's last checkpoint is intact. The engine preserves an incomplete or
damaged prefix rather than guessing whether it was a new, uncommitted WAL or
a damaged WAL that previously held commits. A failed initial identity flush
cannot publish transaction frames; successful commits durably flush their
prefix together with the transaction. Keep both files when reporting recovery
errors, including short WAL files.
Damage to the data-header identity write can likewise require manual recovery;
the engine does not replace damaged identity metadata with a guessed value.

Legacy v8 files and WALs without identity metadata can still be opened. Where
a legacy WAL contains a confirmed header, its creation time must match the data
file. Legacy records cannot prove checkpoint generation, and a legacy WAL
without a header cannot prove database identity. Protection starts after an
empty WAL is bound or a successful checkpoint migrates the file. During that
checkpoint the legacy WAL is durably truncated before identity is installed,
so interrupted migration does not leave an ambiguous protected/legacy pair.

Older engines can read ordinary v8 files and ignore the unconfirmed identity
prefix. Checkpoint and close an older writer before reopening its files with
this engine: a protected data file paired with an older writer's new prefixless
WAL is rejected because its generation cannot be established. Older writers
do not advance this generation metadata, so generation guarantees do not extend
across their checkpoints. Preserve the original pair and use the writer that
created such a WAL to checkpoint it; do not delete an unverified WAL to bypass
the error.
