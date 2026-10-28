# Torn compact-promotion journal recovery (#3137)

A caller data stream tears the first compact-promotion header write after 59
physical bytes and zeroes the rest. The preceding journal and committed WAL
remain intact. Both plaintext and encrypted images then reopen as real files:
two read-only attempts preserve both sources; two writable attempts retain all
four complete rows, the secondary index and the WAL-only collection.

The known-bad state is PR #3137 commit `5d3cc5c248b9fa40f6793783b70a9780ebedc773`:
it refuses the durable journal because the primary identity and salt were torn
away (exit 0). The candidate recovers (exit 1). Unexpected outcomes exit 2.
A clean legacy-storage control must recover before injecting the tear. The
repro uses public caller-stream I/O rather than internal fault hooks.

`PromotionJournalAuthority_Tests` additionally models lost volatile writes and
two torn repairs before real-file reopen; the unchanged `compact-power-loss`
target exercises the original seeds. These are modeled faults, not a device
reset campaign or authentication of arbitrarily substituted damaged files.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3137_TornPromotionJournal
```

See `.github/safety/regression-proofs.json` for the pinned proof and permanent guards.
