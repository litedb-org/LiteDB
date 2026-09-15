# Issue 2793: real Windows account comparison

On a disposable Windows test machine with administrator rights and .NET 8/10:

```powershell
./tools/Issue2793Windows/run.ps1 -OutputDirectory C:/temp/issue2793-results
```

The driver creates an isolated temporary directory, local account, and LocalSystem
scheduled task, then removes them in `finally`. It runs three comparisons each
against 5.0.19 and current source. Direct file-access controls, actual owner/peer
SIDs, held-mutex blocking, exact exit codes, and an independently reopened ledger
are mandatory. Historical shared-mutex denial is expected; source denial fails
the run. No unknown outcome or unrelated file-access denial is accepted.

The workflow `.github/workflows/issue-2793.yml` runs this on Windows CI.
[Run 34921553080](https://github.com/litedb-org/LiteDB/actions/runs/34921553080)
observed all three historical failures and all three source passes. This confirms
the cross-account case only; it does not run inside a UWP/AppContainer token.
