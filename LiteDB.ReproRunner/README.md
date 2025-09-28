# LiteDB ReproRunner

The LiteDB ReproRunner is a small command-line harness that discovers, validates, and executes the
**reproduction projects** ("repros") that live in this repository. Each repro is an isolated console
application that demonstrates a past or current LiteDB issue. ReproRunner standardises the folder
layout, metadata, and execution model so that contributors and CI machines can list, validate, and run
repros deterministically.

## Repository layout

```
LiteDB.ReproRunner/
  README.md                         # You are here
  LiteDB.ReproRunner.Cli/           # The command-line runner project
  Repros/                           # One subfolder per repro
    Issue_2561_TransactionMonitor/
      Issue_2561_TransactionMonitor.csproj
      Program.cs
      repro.json
      README.md
```

Each repro folder contains:

* A `.csproj` console project that can target the NuGet package or the in-repo sources.
* A `Program.cs` implementing the actual reproduction logic.
* A `repro.json` manifest with metadata required by the CLI.
* A short `README.md` describing the scenario and expected outcome.

## Quick start

Build the CLI and list the currently registered repros:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- list
```

Show the manifest for a particular repro:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- show Issue_2561_TransactionMonitor
```

Validate all manifests (exit code `2` means one or more manifests are invalid):

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- validate
```

Run a repro against both the packaged and source builds:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run Issue_2561_TransactionMonitor
```

## Writing a new repro

1. **Create the folder** under `LiteDB.ReproRunner/Repros/`. The folder name should match the manifest
   identifier, for example `Issue_1234_MyScenario/`.

2. **Scaffold a console project** targeting `.NET 8` that toggles between the NuGet package and the
   in-repo project using the `UseProjectReference` property:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net8.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
       <UseProjectReference Condition="'$(UseProjectReference)' == ''">false</UseProjectReference>
       <LiteDBPackageVersion Condition="'$(LiteDBPackageVersion)' == ''">5.0.20</LiteDBPackageVersion>
     </PropertyGroup>

     <ItemGroup Condition="'$(UseProjectReference)' == 'true'">
       <ProjectReference Include="..\..\..\LiteDB\LiteDB.csproj" />
     </ItemGroup>

     <ItemGroup Condition="'$(UseProjectReference)' != 'true'">
       <PackageReference Include="LiteDB" Version="$(LiteDBPackageVersion)" />
     </ItemGroup>
   </Project>
   ```

3. **Implement `Program.cs`** so that it:

   * Performs the minimal work necessary to reproduce the issue.
   * Exits with code `0` when the expected (possibly buggy) behaviour is observed.
   * Exits non-zero when the repro cannot trigger the behaviour.
   * Reads the orchestration environment variables:
     * `LITEDB_RR_SHARED_DB` – path to the shared working directory for multi-process repros.
     * `LITEDB_RR_INSTANCE_INDEX` – zero-based index for the current process.

4. **Author `repro.json`** with the required metadata (see schema below). The CLI refuses to run
   invalid manifests unless `--skipValidation` is provided.

5. **Add a `README.md`** to document the scenario, relevant issue links, the expected outcome, and any
   local troubleshooting tips.

6. **Update CI** if the repro should be part of the smoke suite (typically red or flaky repros).

## Manifest schema

Every repro ships with a `repro.json` manifest. The CLI validates the manifest at discovery time and
exposes the metadata in the `list`, `show`, and `run` commands.

| Field                | Type          | Required | Notes |
| -------------------- | ------------- | -------- | ----- |
| `id`                 | string        | yes      | Unique identifier matching `^[A-Za-z0-9_]+$` and the folder name. |
| `title`              | string        | yes      | Human readable summary. |
| `issues`             | string[]      | no       | Absolute URLs to related GitHub issues. |
| `failingSince`       | string        | no       | First version that exhibited the failure (e.g. `5.0.x`). |
| `timeoutSeconds`     | int           | yes      | Global timeout (1–36000 seconds). |
| `requiresParallel`   | bool          | yes      | `true` when the repro needs multiple processes. |
| `defaultInstances`   | int           | yes      | Default process count (`≥ 2` when `requiresParallel=true`). |
| `sharedDatabaseKey`  | string        | conditional | Required when `requiresParallel=true`, used to derive a shared working directory. |
| `args`               | string[]      | no       | Extra arguments passed to `dotnet run --`. |
| `tags`               | string[]      | no       | Arbitrary labels for filtering (`list` prints them). |
| `state`              | enum          | yes      | One of `red`, `green`, or `flaky`. |

Unknown properties are rejected to keep the schema tight. The CLI also validates that each repro
folder contains exactly one `.csproj` file.

### Validation output

`repro-runner validate` prints `VALID` or `INVALID` lines for each manifest. Errors are grouped under
an `INVALID` header that includes the relative path. Example:

```
INVALID  Repros/Issue_999_Bad/repro.json
  - $.timeoutSeconds: expected integer between 1 and 36000.
  - $.id: must match ^[A-Za-z0-9_]+$ (got: Issue 999 Bad)
```

`repro-runner list --strict` returns exit code `2` when any manifest is invalid; without `--strict`
it merely prints the warnings.

## CLI usage

```
Usage: repro-runner [--root <path>] <command> [options]
```

Global option:

* `--root <path>` – Override the discovery root. Defaults to the nearest `LiteDB.ReproRunner` folder.

Commands:

* `list [--strict]` – Lists all discovered repros with their state, timeout, tags, and titles.
* `show <id>` – Dumps the full manifest for a repro.
* `validate [--all|--id <id>]` – Validates manifests. Exit code `2` indicates invalid manifests.
* `run <id> [options]` – Executes a repro (see below).

### Running repros

```
repro-runner run <id> [--instances N] [--timeout S] [--skipValidation]
```

* `--instances` – Override the number of processes to spawn. Must be ≥ 1 and ≥ 2 when the manifest
  requires parallel execution.
* `--timeout` – Override the manifest timeout (seconds).
* `--skipValidation` – Run even when the manifest is invalid (execution still requires the manifest to
  parse successfully).

Each invocation plans deterministic run directories under the CLI’s output folder
(`bin/<tfm>/<configuration>/runs/<manifest>/<variant>`), cleans any leftover artifacts, and prepares all
builds before execution begins. Package and source variants are compiled in Release mode with
`UseProjectReference` flipped appropriately, and their artifacts are placed inside the planned run
directories. Execution then launches the built assemblies directly so that run output stays within the
per-variant folder. All run directories are removed once the command completes, even when failures or
cancellations occur.

Timeouts are enforced across all processes. When the timeout elapses the runner terminates all child
processes and returns exit code `1`. Build failures surface in the run table and return a non-zero exit
code even when execution is skipped.

## Parallel repros

Set `requiresParallel` to `true` and choose a descriptive `sharedDatabaseKey`. The key is used to build
a deterministic folder for shared resources (for example, a LiteDB data file). Each process receives:

* `LITEDB_RR_SHARED_DB` – The shared directory path rooted under the variant’s run folder (for example
  `runs/Issue_1234_Sample/ver_latest/run/<guid>`).
* `LITEDB_RR_INSTANCE_INDEX` – The zero-based process index.
* `LITEDB_RR_TOTAL_INSTANCES` – Total instances spawned for this run.

The repro code is responsible for coordinating work across instances, often by using the shared
folder to create or open the same database file.

## CI integration

The GitHub Actions workflow builds the repository in Release mode, then runs:

1. `repro-runner list --strict`
2. `repro-runner validate`
3. A smoke repro run (initially `Issue_2561_TransactionMonitor`)

Any non-zero exit code fails the job, ensuring the manifests stay valid and at least one repro is
exercised on every PR.

## Troubleshooting

* **Manifest fails validation** – Run `repro-runner show <id>` to inspect the parsed metadata. Most
  errors reference the JSON path that needs attention.
* **Build failures** – When `repro-runner run` fails to build, the `dotnet build` diagnostics for each
  variant are printed after the run table. Use the variant label (package vs latest) to pinpoint which
  configuration needs attention.
* **Timeouts** – Use `--timeout` to temporarily raise the limit while debugging long-running repros.
* **Custom LiteDB package version** – Pass `-p:LiteDBPackageVersion=<version>` through the CLI to
  target a specific NuGet version. The property is forwarded to both build and run invocations.

## FAQ

**Why not use xUnit?** Repros often require multi-process coordination, timeouts, or package/project
switching. The CLI gives us deterministic orchestration without constraining the repro to the xUnit
lifecycle. Test suites can still shell out to `repro-runner` if desired.

**How do I mark a repro as fixed?** Update `state` to `green`, adjust the README, and ensure the repro
now exits `0` when the fix is present. Keeping the repro around prevents regressions.

**Can I share helper code?** Keep repros isolated so they are self-documenting. If you must share
helpers, place them next to the CLI and avoid coupling repros together.

## Further reading

* [Issue 2561 – TransactionMonitor finalizer crash](https://github.com/litedb-org/LiteDB/issues/2561)
* `LiteDB.ReproRunner.Cli` source for the discovery/validation logic.
