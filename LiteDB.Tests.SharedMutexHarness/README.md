# SharedMutexHarness

Console harness that stress-tests LiteDB’s `SharedMutexFactory` across processes and Windows sessions.

## Getting Started

```bash
dotnet run --project SharedMutexHarness/SharedMutexHarness.csproj
```

The parent process acquires the shared mutex, spawns a child that times out, releases the mutex, and spawns a second child that succeeds.

## Cross-Session Probe (PsExec)

Run the parent from an elevated PowerShell so PsExec can install `PSEXESVC`:

```powershell
dotnet run --project SharedMutexHarness/SharedMutexHarness.csproj -- --use-psexec --session 0
```

- `--session <id>` targets a specific interactive session (see `qwinsta` output).
- Add `--system` to launch the child as SYSTEM (optional).
- Use `--log-dir=<path>` to override the default `%TEMP%\SharedMutexHarnessLogs` location.

Each child writes its progress to stdout and a per-run log file; the parent echoes that log when the child completes so you can confirm whether the mutex was acquired.
