Persisted eight asymmetric, saturated 9-node cliques and their one-way center.
Writer: PR #3142 at 6b2ecd3af26829c52d964da9dc0c33d67c7753a4, Release net8, TestingEnabled=true.
Setup: unchanged V-HNSWb Review2_delete_with_asymmetric_saturated_components_does_not_throw, checkpoint before delete.
The old writer throws Queue empty on delete; the candidate must defer atomically and answer from current documents.
SHA256 (uncompressed): f0f99c8b50ed7a47eb9786ba38a4ba1e261a6d5398b418766df1cd654c393781
