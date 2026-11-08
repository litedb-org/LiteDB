# Vector finding — product bug (known, open #2952)

Exact candidate e507faad40e968aa2fcf63ae6dd01fd7def2e07b, Linux x64/.NET8. Three original seed failures and three independent hosted replays have identity VECTOR_DATABASEVECTORINTEGRITYVERIFIER_L43. This is an existing open product bug https://github.com/litedb-org/LiteDB/issues/2952 (duplicate #2980 closed); candidate known-findings.json explicitly records it. No infrastructure or flaky classification.

| Seed | Original step | Hosted replay prefix | Further minimizer prefix |
| --- | ---: | ---: | ---: |
|529476|101|101|101|
|529477|101|100|80|
|529478|303|303|303|

Minimal independently replayed campaign repro: `dotnet LiteDB.Fuzz.dll --target vector --seed 529477 --count 100 --artifact-dir repro` using the retained net8.0 binary bundle. The replay's own minimizer found80; that is a same-identity minimization trial, not a separate full hosted80-step run.

Exact recorded-input replay: from candidate checkout or downloaded complete binary bundle, `dotnet LiteDB.Fuzz.dll --replay <artifact>/evidence/fuzz/vector/529477/20261108-225417-vector-s529477-w0-r0e1e8368/minimized-replay.json --artifact-dir repro`. Preserve input.bin beside the replay file. Full original and replay errors, graph/database bytes, traces and hashes live under artifacts/p5-linux-4/evidence/{fuzz,replays}; replay-index.json and commands.json record every invocation.

The raw verifier independently enumerates allocated vector nodes and follows the persisted root; live node0004:21 points to primary-data block0003:21 outside the reachable component in the original failure. The independent100-step replay reports live node0004:23 ->0003:21 outside the component. Node addresses can differ at the shortened prefix; failure identity is unchanged. VectorIndexService.RemoveNode removes backlinks without reconnecting graph components; Search and TryFindNode walk from root. Classification: product bug, matching open2952, with possible ANN omission and index-maintenance failure. Product sources remain unchanged.

The ordinary candidate fuzzer classifies this as known and returns success; the external campaign driver deliberately records replay exit1 and makes the shard red. A red shard here is evidence of reproduced product failures, not a new unclassified infrastructure failure. No claim of a newly introduced Phase4 regression.

## PR3142 replay comparison (round 2)

PR https://github.com/litedb-org/LiteDB/pull/3142 branch `fix/hnsw-graph-connectivity`, head `c2776273fe490d8e06d58a68e2d59fb85685ba51`. Built isolated unmodified source archive, net8 Release TESTING; build 0 errors (15 warnings), command/log `pr3142-build.log`. No product edits.

All three seeds529476/529477/529478 pass 400 steps on this head; seed529477 also passes100 and80 steps. Commands, exit codes and exact binary SHA256 are in `pr3142-replays/commands.json` and `sha256.json`; run.json files record passed assertions. `pr3142-input-prefix-comparison.json` proves regenerated input begins byte-for-byte with the entire original failing input (2580/3184/19572 bytes respectively). Thus the same failing inputs are exercised, followed by the remaining generated operations.

Raw original `--replay` commands report input exhaustion after passing the old failure boundary: retained input stops at the original failure although replay Count=400. Bounding Count to101/101/303 also exhausts input in final post-loop validation, which consumes additional random vectors. These are classified replay-envelope/input-length limitations, not graph failures; all raw logs and failed envelopes are retained under pr3142-replays/recorded-* and bounded-*. Source archive git-provenance probes report no .git; fetched full revision plus build command and binary hashes establish provenance independently.

Conclusion: **#3142 fixes the #2952 manifestation exercised by these three seeds and the100-step minimal prefix: yes**. This bounded finding does not establish every scenario in2952 is fixed, qualify Phase4 plus3142 as an integrated tree, or establish cross-platform/performance coverage of3142. No separate vector product defect remains in these replay cases.
