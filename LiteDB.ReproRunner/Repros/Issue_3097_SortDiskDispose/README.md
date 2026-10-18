# Issue 3097: sort scratch file survives a stream-close failure

[Issue #3097](https://github.com/litedb-org/LiteDB/issues/3097) reports that `SortDisk.Dispose()` closes its stream pool before deleting the `-tmp` scratch file. If a stream's `Dispose()` throws, control skips the delete call and the file remains. The fix places deletion in `finally`, so it is attempted even when closing a pooled stream fails.

## What this repro checks

The repro commits 1,100 documents, starts an unindexed sort with large keys, and confirms that a nonempty scratch file exists. It closes the query enumerator to return its leased reader, then puts a real `FileStream` into the idle reader pool using reflection. That stream closes its OS handle and throws `IOException` from `Dispose()`. After engine disposal, the repro checks whether the scratch file remains **before reopening the database**, then reopens twice and compares every committed id and key with the original input.

| Variant | Required observation | Marker and exit code |
| --- | --- | --- |
| Known-bad dev revision | The injected close fails and the scratch file remains. | `ISSUE_3097_REPRODUCED`, exit 0 |
| Current source with the fix | The injected close fails and the scratch file is deleted. | `ISSUE_3097_FIXED`, exit 1 |
| Invalid setup or integrity check | No valid conclusion about the bug. | `ISSUE_3097_HARNESS_ERROR`, exit 2 |

Here, “reproduced” means the **bug** occurred. Exit 1 is the expected fixed result for a direct run; it is not an unexpected test failure. ReproRunner requires the marker and exit code specified in `repro.json`, so a harness error cannot count as a fix.

The package variant pins dev commit `7b71bc4dd45b48a1df4418ddb4cd245832273b35`, where the issue was verified. This is a specially built known-bad package, not a published release or the first version affected. The repro depends on private sort and stream-pool fields; compatibility with a published package has not been established.

## Run it

Open `Issue_3097_SortDiskDispose.csproj` directly in Visual Studio to run against the current LiteDB source. The project defaults to a project reference. The expected output is `ISSUE_3097_FIXED` with exit code 1.

To compare both variants, run these commands from the repository root in PowerShell:

```powershell
python .github/scripts/regression_proof.py pack-known-bad --commit 7b71bc4dd45b48a1df4418ddb4cd245832273b35 --feed artifacts_temp/issue3097/feed
$env:RestoreAdditionalProjectSources = (Resolve-Path artifacts_temp/issue3097/feed).Path
$env:TestingEnabled = 'true'
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -p:TestingEnabled=true -- run Issue_3097_SortDiskDispose --ci --report artifacts_temp/issue3097/proof.json
python .github/scripts/regression_proof.py verify --report artifacts_temp/issue3097/proof.json --repro Issue_3097_SortDiskDispose --expect-version 0.0.0-knownbad.7b71bc4dd45b
```

The permanent unit guard is `LiteDB.Tests/Internals/SortDiskLifecycle_Tests.cs#Dispose_DeletesScratchFile_AndPreservesDatabase`. Its three cases cover ordinary disposal and failures while closing a reader or writer. They assert scratch deletion, propagation of the original close exception at the `SortDisk` boundary, safe repeated disposal, unchanged database bytes, and committed data after reopening. The two failure cases failed against the pre-fix implementation because the scratch file remained; they pass with the `finally` change.

## Scope

This proof isolates the skipped-delete path **after the query releases its reader**. The issue's sample keeps a query reader active during engine disposal. On Windows, that open handle can independently block deletion; the literal active-reader scenario still left a scratch file with the minimal fix during local validation. This proof does not claim to resolve that reader-lifetime case.

`finally` guarantees a deletion attempt, not deletion when another handle holds the file or the filesystem rejects it. If deletion throws, its exception may replace the stream-close exception. The separate engine-level error-reporting concern in [issue #3047](https://github.com/litedb-org/LiteDB/issues/3047) is unchanged. This repro does not cover process death or power loss.
