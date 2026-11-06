Writer: PR #3142 at 6b2ecd3af26829c52d964da9dc0c33d67c7753a4, Release net8, TestingEnabled=true.
Setup: existing BuildBoundedRepairGraph(2049, arms: true), committed docs.Delete(1), checkpoint.
The bit was set by the real bounded deletion path, with 2048 live documents.
