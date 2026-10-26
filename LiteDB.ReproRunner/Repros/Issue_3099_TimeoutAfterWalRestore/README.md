# TIMEOUT after WAL header recovery (#3099)

Public Direct database repro: the data header retains the default 60-second
TIMEOUT while a nonempty WAL carries 1 second. Also checks setting TIMEOUT after
WAL recovery, clean-open controls, plain/encrypted files, rollback, indexed rows,
untouched sentinel data, and checkpoint/reopen. Real collection contention is
released after four seconds so the baseline never needs to sleep 60 seconds.

Package 6.0.0-prerelease.322 exits 0 with PROVEN_STALE_TIMEOUT. Fixed source exits
2 with FIXED_VERIFIED. Unexpected failures exit 1 and satisfy neither outcome.
TimeoutAfterWalRestore_Tests additionally exercise transaction admission and
exclusive gate contention, repeated setters, and waits after checkpoint/reopen.
