# CRC-valid damaged vector metadata changes the declared metric

The current file format stores the vector index metric in collection-page metadata. A page checksum can remain valid while that metadata is logically damaged. Before this fix, a malformed vector section was discarded and an Euclidean query silently fell back to cosine; writes were acknowledged without maintaining the declared vector index.

The repro creates a current Euclidean index, changes the vector metadata name to invalid UTF-8, recomputes the page CRC, and then performs the same nearest-neighbour query and insert against the damaged file.

The known-bad `dev` commit returns the wrong row and acknowledges the insert. The candidate refuses both operations with an actionable error and preserves the data and log bytes.
