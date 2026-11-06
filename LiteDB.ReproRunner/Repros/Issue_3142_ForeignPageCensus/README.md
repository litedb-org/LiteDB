# Foreign pages after a vector writer pin

Reproduces the WAL-version census failure in PR #3142 at `2e89d62bc95adf0ac2b4d8c5caadf8c9ab747f37`.
An explicit vector update pins a write snapshot, another thread commits a 40k foreign payload, then vector deletion must succeed.
The harness verifies engine liveness, unrelated committed bytes, document identities and cold reopen.
Known bad prints `BUG_VERIFIED` / exits 0; current source prints `FIXED_VERIFIED` / exits 2. Other errors fail the harness.

Prepare the synthetic package (the CI proof workflow does this automatically):

```bash
python3 .github/scripts/regression_proof.py pack-known-bad --commit 2e89d62bc95adf0ac2b4d8c5caadf8c9ab747f37 --feed /tmp/litedb-knownbad-feed
export RestoreAdditionalProjectSources=/tmp/litedb-knownbad-feed
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_3142_ForeignPageCensus
```
