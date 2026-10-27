# CRC-valid damaged vector metadata changes the declared metric

The current file format stores the vector index metric in collection-page metadata. A page checksum can remain valid while that metadata is logically damaged. Before this fix, a malformed vector section was discarded and an Euclidean query silently fell back to cosine; writes were acknowledged without maintaining the declared vector index.

The repro creates a current Euclidean index, changes the vector metadata name to invalid UTF-8, recomputes the page CRC, and then performs the same nearest-neighbour query and insert against the damaged file on two reopens. It checks both unsafe outcomes before reporting reproduction, so a correct query refusal cannot hide a write-admission regression.

The known-bad `dev` commit returns the wrong row and acknowledges the insert. The candidate refuses both operations with an actionable error and preserves the data and log bytes.

The permanent matrix guard covers all six metadata damage kinds, plain/encrypted files, current files and files migrated from the published prerelease fixture, plus legacy migration refusal and explicit rebuild. Healthy metric controls assert the vector plan and independent Euclidean/cosine/dot-product results. The known-bad comparison uses unmodified `dev` commit `b3c4406231908c6ca29f6d4b2769aa08351d6fc2`; the candidate requires `INVALID_DATAFILE_STATE`, collection/index names and recovery guidance, and compares data/WAL bytes after every refusal.
