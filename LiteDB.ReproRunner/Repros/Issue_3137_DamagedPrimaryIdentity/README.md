# Torn modern primary identity refuses valid journal recovery

The proof captures a real modern checkpoint journal using public caller-stream
I/O, then changes the primary creation timestamp by one tick without repairing
its checksum. This is indistinguishable from a same-database header tear.
The known-bad PR commit `fc39c52da8c5c6bab4ae5baffdb5ae81b0aa3a99` refuses
both writable and read-only opens without changing either file (exit 0).
The candidate recovers all committed rows, indexed lookup and the WAL-only
collection across two opens; read-only sources remain identical (exit 1).
Unexpected results exit 2. An unchanged-primary positive control must recover.

This proof replaces the earlier incorrect assumption that creation+1 on a
checksum-invalid header proved a foreign database. The generation test retains
all 20 original damage cases as recovery checks, and intact conflicting creation
identity still refuses. The separate prepared-suffix proof retains its original
known-bad state and preserving-refusal oracle.

Fault model: synthetic corrupt/torn primary bytes with a retained valid journal,
not hardware power loss. Substitution plus identity damage is unauthenticated,
as on dev; ordinary data/index pages have no database identity. See
`docs/rules/compatibility.md` and `.github/safety/regression-proofs.json`.
