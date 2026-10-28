# Normal 20,000-vector HNSW checkpoint

Writer: PR #3142 at `6b2ecd3af26829c52d964da9dc0c33d67c7753a4`, Release net8, TestingEnabled=true.
Recipe: insert ids 1..20000 into `docs` with `Embedding = [id % 97, id % 31, 1, -1]`, then
`EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(4))` and checkpoint before any mutations.
Metric: default cosine. The normal public writer constructs the graph; no adjacency or metadata is patched.
Expected initial state: all 20,000 exact vectors, one healthy vector index, no pending repair or WAL.
Initial random level draws are frozen by the checkpoint bytes.
Permanent workload tests seed both operation selection and later level draws with 91731 and all 1,000 original delete / alternating update operations,
verify every survivor payload and identity, availability and zero deferral. Caching only setup avoids
repeating quadratic normal-index construction in each 300-second CI session.

Uncompressed SHA256: `e28216f8fa75469147a0e64d674b9dcfec436b681b9ca17952551c9e56a05789`
