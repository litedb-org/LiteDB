# Issue 2794: two-process job handoff

The #1742 report consolidated into #2794 describes a WPF client setting a
nullable deletion date and a Windows service subsequently completing the same
job. This control runs actual client/service processes against one Shared-mode
database. Windows workers target .NET Framework 4.8, matching the reported
framework family; Linux workers use .NET 8. The .NET 8 parent only coordinates
processes. The historical variant uses the reported LiteDB 5.0.7 package.

Each of three attempts starts with a fresh database and two workers held at a
start barrier. Across 24 rounds, the client and service each update all 24 jobs
through normal `Update` calls: 1,152 acknowledged updates per attempt. The nullable
date fields follow the reported handoff. Patterned payloads and Guid dictionaries
are additional controls that grow/shrink valid documents across page boundaries;
they are not a reconstruction of the unavailable complete customer model.

Every read checks all fields against independently generated values. The client
checks the complete scan and every primary-key lookup after each round. A fourth,
fresh process verifies all final jobs through a read-only connection, and the
parent verifies the data-file hash remains unchanged. Workers log their PID,
framework target, package assembly version, and completion marker.
The five date fields use the exact BSON names shown in #1742; raw-BSON checks
independently verify those names, types, and UTC values at seed and final reopen.

```sh
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_2794_SharedJobHandoff --report issue2794.json --report-format json
```

Success is exit 10 and records only a passing bounded control. Unknown exceptions,
missing work, incomplete ledgers, and timeouts fail the harness with exit 20;
they are not automatically classified as the reported corruption. The original
customer workload, Windows version, interruption/storage history, and #1603's
complete directory application remain unknown. This test cannot establish that
the originating corruption is fixed.

The handoff gives each job one writer at a time, so each individual update is
atomic without an enclosing read/write transaction. A separate initial probe
with explicit transactions passed 5.0.7 but hit the known Shared-mode transaction
mutex retention on dev (#2787). That timeout is not the BSON corruption reported
in #2794 and is not counted as its reproduction.
