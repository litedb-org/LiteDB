# Shared transaction retry writer release (#3094)

Runs the public duplicate-key → BeginTrans → Commit sequence on plain and encrypted
Shared databases. The original owner stays alive while another thread counts and
writes through the same instance and a peer. Ordinary transactions are controls.

The package variant pins 6.0.0-prerelease.322. Exit 0 with PROVEN_BLOCK identifies
the leaked ownership; exit 2 with FIXED_VERIFIED requires both writers and cold
row verification. Unexpected errors exit 1 and satisfy neither expectation.
The normal SharedRetryWriterRelease_Tests also cover child processes, recursion,
pinned holds, failed Begin, repeated retries, rollback, and ownership guards.
