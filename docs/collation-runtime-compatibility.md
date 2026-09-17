# Collation runtime compatibility

Culture-sensitive string indexes depend on the runtime's sort tables, not just
the stored culture and comparison options. Moving a file between ICU, NLS,
invariant globalization, or sort-table versions can change index ordering and
uniqueness. LiteDB now rejects a detected mismatch before admitting queries or
writes, with rebuild guidance. A collation mismatch does not trigger automatic rebuild or discard rows that
become equal under a new comparer.

New and rebuilt files on runtimes with sort-version support store a 32-bit hash in reserved header bytes 92–95. The hash
covers comparison options, culture, runtime sort version/ID, and comparison probes.
Pure Ordinal comparison has a runtime-independent signature. Ordinary files stay
on format v8; vector files stay on v9. No WAL identity bytes are reused. Checks
apply to the data header and the recovered WAL header. Ordinary opening and collation validation do not rewrite the stamp.
Existing explicitly requested `AutoRebuild=true` recovery of files marked corrupt
still rebuilds before opening (also with `ReadOnly=true`). That recovery regenerates
indexes from records using the current comparer; conflicting unique keys abort
before the replacement is installed. Keep its backup and inspect recovery errors.

Legacy files with a zero stamp have every ordinary index traversed at level zero
before the engine opens. Non-monotonic keys or equal keys in a unique index cause
the same rejection. This scan is read-only and costs time proportional to the
index entries on every open, including shared-mode opens. Runtimes without a
sort-version implementation (such as Mono 6.12) also use this zero-stamp path.
Rebuild a legacy file
in its original environment to create a stamped file and avoid repeated scans.

To move a database safely, open it in the original compatible environment and
call `Rebuild(new RebuildOptions { Password = existingPassword,
Collation = Collation.Binary })`, then move the data file. `Collation.Binary` uses
the invariant culture with Ordinal comparison. Changing collation can change
application query semantics; alternatively export records in the original
environment and import them into a new database in the destination environment.
Keep the rebuild backup and resolve any target unique-key conflicts explicitly.
A mismatch cannot be repaired by calling `Rebuild()` on a rejected connection.

The stamp is a compact compatibility signature, not a cryptographic integrity
check. Older LiteDB versions ignore it and must not write the file under a
different comparer; their writes cannot update this metadata. Damaged indexes or
arbitrary modifications are outside this signature's guarantee. Cross-process
validation covers ICU versus invariant globalization in both directions, with
read-only byte preservation and duplicate-sensitive upsert controls.
