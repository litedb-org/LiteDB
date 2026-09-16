# Issue 1087 - Linux lock errors bypass the retry timeout

This two-process repro covers [LiteDB #1087](https://github.com/litedb-org/LiteDB/issues/1087).
Linux reports a conflicting `FileStream.Lock` as `IOException` error 11 (`EAGAIN`). LiteDB's
`IOExceptionExtensions.IsLocked` recognizes only the Windows sharing and lock codes 32 and 33, so
`FileHelper` propagates Linux contention immediately instead of retrying it for the configured timeout.

## Contract and independent controls

Process 0 acquires a real byte-range lock and holds it until process 1 requests a delayed release.
Process 1 then checks all of the following before it declares the issue reproduced:

1. A direct `FileStream.Lock` attempt fails while process 0 owns the range with native error code 11,
   while another direct attempt succeeds after release. Together these prove the OS enforces the
   collision and that the contender is otherwise able to acquire the exact same range.
2. LiteDB recognizes Windows lock code 32 as a positive classifier control, then rejects a non-lock
   code-2 `IOException` sentinel after exactly one attempt. This guards against a broad "retry every
   I/O error" change making the main scenario pass while masking unrelated faults.
3. LiteDB's lock classifier rejects the real error-11 exception, and its retry helper invokes the
   lock action only once even though the owner releases the range shortly afterward.

The repro emits `BUG_1087_CONFIRMED` and exits 0 only when all three observations agree. A correct fix
must classify the real collision, retry it at a bounded cadence, wait for process 0's controlled
release, and actually acquire the range; that path emits `NO_BUG_1087` and exits 10. Missing or changed
internal plumbing is reported as a harness failure, not as a successful reproduction.

## Run

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
  run Issue_1087_LinuxLockRetry --ci --target-os ubuntu-24.04
```

The package variant uses LiteDB 4.1.4, from the affected 4.1 line. The latest variant links the current
repository source. The manifest deliberately schedules this repro only on Linux and runs exactly two
instances for each variant.

## Historical reproduction on dev

**REPRODUCED** on Linux x64 (Ubuntu 24.04, ReproRunner target `ubuntu-24.04`) at exact source SHA
`a7ac43a0f7e0e002138ad909f1f91432a1f709e7`.

Manifest validation passed with exit code 0:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- validate --id Issue_1087_LinuxLockRetry
```

The two-variant run also passed its declared reproduction expectations with exit code 0:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
  run Issue_1087_LinuxLockRetry --ci --target-os ubuntu-24.04
```

Both the affected LiteDB 4.1.4 package and the current-source (`latest`) variant emitted
`BUG_1087_CONFIRMED` and exited 0. In both variants, the real cross-process collision had
`HResult=0x0000000B` / native code 11, LiteDB reported `classifiedAsLocked=False`, and its retry helper
made exactly one attempt before propagating `IOException` (`acquired=False`, `elapsedMs=0`). After the
owner's controlled release, the independent direct-lock control acquired the same range successfully
(`postReleaseControlAcquired=True`).

## Manual fix verification (2026-09-16)

Current source recognizes raw Linux errno 11 as a retryable lock collision while
leaving wrapped Win32 error 11 and unrelated I/O failures non-retryable. The latest
manifest now requires exit 10 and `NO_BUG_1087`; package 4.1.4 still requires its
original bug outcome.

The same two-process run passes both expectations. Package 4.1.4 makes one attempt
and fails immediately. Fixed source classifies the actual collision, makes 21
attempts over 504 ms, and acquires the lock after the owner's controlled release.
The independent post-release acquisition and immediate non-lock-error controls
also pass. Focused tests additionally cover both retry helpers and timeout expiry.
