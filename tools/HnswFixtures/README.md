# HNSW old-writer fixtures

Run `python3 scripts/generate-hnswc-fixtures.py --output /absolute/fresh/directory`
from this checkout. The script creates an isolated worktree at the real pre-fix
writer `6b2ecd3af26829c52d964da9dc0c33d67c7753a4`, overlays the focused producer,
and runs only its three producer tests on net8 with the repository runsettings.
It retains the producer checkout, TRX, databases, ZIPs and SHA-256 manifest.
The normal 20k index can take several minutes to construct. This is fixture
production, not a benchmark; no elapsed-time acceptance criterion is used.

The asymmetric fixture deliberately installs the old graph described by the
reviewer's reproduction. The deferred fixture uses the original bounded-arm
helper and real committed delete. The normal fixture uses public document
insertion and index construction without graph patching. Database page IDs and
random HNSW levels can vary on regeneration; the checked-in artifacts are frozen
historical inputs, and their exact hashes are pinned in
`LiteDB.Tests/Resources/artifacts.json`.

Publish generated ZIPs and their provenance to `litedb-org/LiteDB-Artifacts`,
then pin the commit and archive hashes in the source manifest. Tests and both
black-box repros resolve them with `ArtifactFixtures.Path`, which verifies every
file. For offline runs set `LITEDB_ARTIFACTS_DIR` to a checkout of that pinned
artifacts commit. Never overwrite historical artifacts in place.

## Runtime portability

The frozen databases contain the producer runtime's ICU collation fingerprint.
An explicit connection-string collation cannot override that fingerprint.
`LiteDB.Tests/Utils/HnswFixtureRuntime.cs` prepares disposable test/repro copies:
it accepts only the four pinned database SHA-256 values, clears bytes 92–95
(the runtime fingerprint), and recomputes the header CRC32C at bytes 14–17.
All other bytes, including every historical graph and document page, are retained.
The original archives, writer commit, generation recipe and manifest hashes remain
unchanged. The fourth input is the embedded disconnected `b3c440623` fixture.

The helper opens the copy read-only through the real engine's legacy scalar-index
ordering validation and requires numeric primary keys with no secondary scalar
indexes. These inputs therefore do not depend on culture-sensitive string order.
It does not install a matching fingerprint or change product validation.
`HnswFixtureRuntime_Tests` checks all four inputs, original preservation, exact
allowed header differences, and rejection after any graph-byte damage.
