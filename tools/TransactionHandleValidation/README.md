# Transaction handle compatibility fixtures

Source, binary and warning compatibility checks for the transaction handles of
[#3064](https://github.com/litedb-org/LiteDB/issues/3064). The legacy `BeginTrans`,
`Commit` and `Rollback` keep their signatures and behavior and are marked
`[Obsolete]` (CS0618, warning only).

[`scripts/test-transaction-handle-compatibility.py`](../../scripts/test-transaction-handle-compatibility.py)
copies this directory outside the repository and:

1. compiles the consumer (`TransactionHandleValidation.csproj`) once against the
   **parent** `LiteDB.dll`, then runs that same binary with the parent DLL and with the
   **candidate** DLL swapped in. Each run checks the loaded DLL's hash.
   - `binary`: `MockDatabase` implements the pre-handle `ILiteDatabase`, and
     `LegacyEngineDecorator` implements the pre-handle `ILiteEngine`. If the candidate
     adds an abstract member to either interface, loading them throws `TypeLoadException`.
     Legacy transactions are run through `LiteDatabase`, the decorator and a caller-owned
     `LiteEngine` with `disposeOnClose: false`. The facade must not dispose the engine, and a
     later facade must see the commit. On the candidate, `LiteTransactionExtensions.BeginTransaction`
     is reached by reflection, so the fixture still compiles against the parent. It must
     throw `NotSupportedException` for the mock and the decorated database, without starting
     a transaction. The `(TimeSpan, CancellationToken)` overload is checked only if it exists.
     It does not exist in the first #3064 slice.
   - `parity`: `LegacyParity` prints the outcomes of legacy-API calls as `parity:` lines
     (nested begin, rollback, statement errors in auto and legacy transactions, foreign-thread
     completion, raw engine, read-only, Direct and Shared files). Exceptions are reported by
     their nearest public type. The parent and candidate transcripts must be identical.
2. builds the consumer with `TreatWarningsAsErrors=true`. Against the parent it must
   succeed. Against the candidate it must fail with `error CS0618`. With the documented
   `WarningsNotAsErrors=CS0618` it must build.
3. builds `NewApi/NewApiConsumer.csproj`, which uses only `BeginTransaction()`, against the
   candidate with `TreatWarningsAsErrors=true`. It must build with no compiler warning and
   run in memory, on a caller-owned engine, on a Direct file and on a Shared file.

Use production libraries (`TestingEnabled=false`) built for the same target framework:

```sh
dotnet build LiteDB/LiteDB.csproj -c Release -f net8.0 -p:TargetFrameworks=net8.0 -p:TestingEnabled=false
python3 scripts/test-transaction-handle-compatibility.py --framework net8.0 \
  --parent-dll /absolute/parent/LiteDB/bin/Release/net8.0/LiteDB.dll \
  --candidate-dll /absolute/candidate/LiteDB/bin/Release/net8.0/LiteDB.dll \
  --output artifacts_temp/transaction-handle-compatibility/net8.0
```

The output directory holds one log per step, both parity transcripts and `manifest.json`.
The manifest records the DLL hashes, the SDK, and each command with its exit code.
The workflow `transaction-handle-compatibility.yml` runs the script on Ubuntu and Windows
for net8.0 and net10.0, using the PR base as the parent.

The oracle was tested with temporary mutants, none committed. An extra abstract member on
`ILiteEngine` or `ILiteDatabase` fails `candidate-binary` with `TypeLoadException`. A legacy
`Rollback()` that always returns true fails the parity comparison.
