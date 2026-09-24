# Issue 2614 – DiskService leaks file handles on initialization failure

This repro enforces a very small per-process file size limit using `setrlimit(RLIMIT_FSIZE)`
and then attempts to create a new database file. LiteDB writes the header page during
`DiskService` construction, so exceeding the limit triggers an exception before the engine
finishes initializing. When the constructor throws, the `DiskService` instance is never
assigned to the engine field and therefore never disposed.

The expected behaviour is that all file handles opened during the initialization attempt
are released so the caller can retry after freeing disk space. Instead, LiteDB leaves the
writer handle open, preventing any subsequent attempt to reopen or delete the data file
within the same process.

## Running the repro

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2614_DiskServiceDispose
```

The process must run on a Unix-like operating system where `setrlimit` is available.
The repro succeeds when it observes:

* The initial `LiteDatabase` constructor throws due to the enforced file size limit.
* The follow-up attempt to reopen the database file exclusively fails because a lingering
  handle is still holding it open.

Reference: <https://github.com/litedb-org/LiteDB/issues/2614>
