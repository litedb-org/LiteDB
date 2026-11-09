# Darwin Shared admission contention

Investigation: retained macOS ARM64 net8 job 129041126114 on phase4
7000dd6f01d0187a8fb5d030064eb327864ab731 threw native error 35 in
DatabaseFileLock.UnixLock -> Lock -> SharedRangeLocks.ShareDms ->
SharedMemoryAuthority.Open, while four processes repeatedly opened Shared databases.

Safety requirements: docs/rules/data-safety.md requires preserved committed data,
docs/rules/storage-ownership.md requires real ownership before admission, and
docs/rules/validation.md requires forced contention plus separate-process evidence.
Implementation and hosted before/after validation are pending.
