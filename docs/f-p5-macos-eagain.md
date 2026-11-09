# Darwin Shared admission contention

Investigation: retained macOS ARM64 net8 job 129041126114 on phase4
7000dd6f01d0187a8fb5d030064eb327864ab731 threw native error 35 in
DatabaseFileLock.UnixLock -> Lock -> SharedRangeLocks.ShareDms ->
SharedMemoryAuthority.Open, while four processes repeatedly opened Shared databases.

Safety requirements: docs/rules/data-safety.md requires preserved committed data,
docs/rules/storage-ownership.md requires real ownership before admission, and
docs/rules/validation.md requires forced contention plus separate-process evidence.
Implementation and hosted before/after validation are pending.

DMS acquisition now retries the existing nonblocking try-lock under a monotonic
60-second budget (the default TIMEOUT). Constructor admission precedes loading
persisted pragmas, so a stored TIMEOUT override is not yet available here. An
explicit internal budget supports bounded contention tests. Timeout throws before
mapping or storage validation and cleanup closes the unsuccessful description.
Native admission-family refusal continues to use fail-fast Lock.

Nonblocking EINTR returns busy to the bounded caller; EACCES and platform EAGAIN
(Darwin EWOULDBLOCK=35, Linux=11) likewise mean contention. Other errors propagate.
No POSIX-lock fallback or kernel-blocking SETLKW is introduced. Apple's command
constants: https://github.com/apple/darwin-xnu/blob/main/bsd/sys/fcntl.h

The process tests cover exclusive DMS release, refusal on timeout and retry,
retained shared ownership, and four default Shared clients repeatedly opening,
inserting, reading and disposing. Cold reopen verifies all160 independently
identified records and an unrelated sentinel. Existing native-admission and
mapped crash/recovery matrices remain required hosted evidence.
