# Test storage

Tests that only need an engine use `:memory:`. Mapper/query tests that need to
serialize and reopen use `MemoryDatabase`, which owns data, WAL and sort streams
across sequential engine instances. Do not share its streams between live engines.

The shared test setup covers `LiteDB.Tests`, `LiteDB.Fuzz.Tests` and
`LiteDB.ReproRunner.Tests`.

Filesystem tests still exercise real `FileStream`, locking, renames, sidecars,
child processes and recovery. Locally, the xUnit framework redirects `TMPDIR`,
`TEMP` and `TMP` to a unique directory on a RAM filesystem before discovery or
execution. This also captures engine-created temporary sort files and `:temp:`
databases. Linux automatically uses `/dev/shm` after checking its filesystem type.
Other platforms require a mounted RAM disk via `LITEDB_TEST_TEMP_ROOT`. A missing
RAM filesystem or invalid configuration stops discovery without falling back to
disk. Use `tests.runsettings` as shown below so VSTest returns a failure when no
tests can be discovered.

```bash
# Linux: RAM-backed test databases and temporary files by default
dotnet test LiteDB.Tests -c Release -f net10.0 -p:TestingEnabled=true --settings tests.runsettings

# Explicit mounted RAM disk (also available on Windows/macOS)
LITEDB_TEST_TEMP_ROOT=/path/to/ramdisk dotnet test LiteDB.Tests -c Release -f net10.0 -p:TestingEnabled=true --settings tests.runsettings

# Explicit opt-in when investigating physical filesystem behavior
LITEDB_TEST_STORAGE=disk dotnet test LiteDB.Tests -c Release -f net10.0 -p:TestingEnabled=true --settings tests.runsettings
```

In PowerShell, set `$env:LITEDB_TEST_TEMP_ROOT = 'R:\'` before running the same
`dotnet test` command. The supplied root must already exist and be absolute;
the caller is responsible for mounting it on RAM. Tests create and clean up only
their own uniquely named child directory. A killed test host may leave that child
directory behind for manual removal. Existing diagnostic retention behavior in
disk mode is unchanged.

`CI=true`/`CI=1` or `GITHUB_ACTIONS=true` defaults to disk and preserves the original
system temp directory and diagnostic volume checks. `LITEDB_TEST_STORAGE=ram`
explicitly overrides that default for harness validation. `disk` overrides the
RAM root setting. Do not set CI flags on a local run intended to avoid disk.

RAM filesystems retain kernel filesystem semantics, unlike a mock filesystem,
but do not test physical-device persistence. Keep file-backed crash, corruption,
flush-failure, upgrade and rebuild tests in CI. No production I/O abstraction is
changed for this test configuration.

This controls **test database and temporary-file I/O**, not SDK restore/build
outputs, test reports, debugger dumps or user-configured diagnostic destinations.
Build outputs and results can separately be placed on a RAM-backed workspace.
On Linux, .NET named mutexes also create small runtime bookkeeping files under
`/tmp/.dotnet/shm`; the runtime does not honor `TMPDIR` for those files. To keep
those writes in RAM too, run in an isolated environment with `/tmp` mounted on
RAM. SDK telemetry is separate and can be disabled with
`DOTNET_CLI_TELEMETRY_OPTOUT=1` before launching `dotnet`.
Linux tmpfs and ordinary managed memory can be swapped; for a strict no-physical-
writes requirement use a suitably sized `noswap` tmpfs mount (where supported)
and account for process swap too. The suite does not change system swap settings
or mount filesystems. RAM capacity must accommodate the test partitions being run.
