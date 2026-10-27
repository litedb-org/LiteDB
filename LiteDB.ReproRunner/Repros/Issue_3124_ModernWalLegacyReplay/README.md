# Modern WAL replayed into legacy data (cross-check F5)

Bounded reproduction found while cross-checking the [v6 roadmap](https://github.com/litedb-org/LiteDB/issues/3124).
It predates PR #3058. The authentic 5.0.21 v8 fixture is converted, a modern confirmed page 3
is generated, and the original legacy data is paired with that WAL truncated before its first trailer.
This intentionally mismatched pair models restore/copy skew or loss of a synced header plus WAL damage;
it does not establish an ordinary crash path on storage that honors sync.

The known-bad dev commit `b3c4406231908c6ca29f6d4b2769aa08351d6fc2` overwrites page 3 and deletes the WAL.
The repro verifies those exact bytes, the missing WAL and persistence across two opens (exit 0).
The candidate must refuse both opens with `INVALID_DATABASE`, preserve both files byte-for-byte,
and still recover every row/payload with the matching modern header (exit 1).
Unexpected outcomes or harness failures exit 2 and cannot count as a proof.
All replayed page IDs are bounded to 3; no sparse-file growth is possible in this scenario.

```bash
python3 .github/scripts/regression_proof.py pack-known-bad \
  --commit b3c4406231908c6ca29f6d4b2769aa08351d6fc2 --feed artifacts_temp/fwalfmt/feed
RestoreAdditionalProjectSources="$PWD/artifacts_temp/fwalfmt/feed" \
  dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_3124_ModernWalLegacyReplay --ci --report artifacts_temp/fwalfmt/proof.json
```

The package is a packed real dev commit; no synthetic mutant substitutes for the historical engine.
The permanent xUnit guards also cover plain/encrypted v8/v9, seven damage states, read-only opens,
later marked committed pages, 32-byte prefixes and genuine 5.0.21 WAL-only commits.
