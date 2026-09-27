# Development

Use this for builds, coding conventions, and release preparation.

## Build and test

```bash
dotnet restore LiteDB.sln -p:TestingEnabled=true
dotnet build LiteDB.sln -c Release -p:TestingEnabled=true
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings
dotnet test LiteDB.Tests -c Release -f net10.0 -p:TestingEnabled=true --settings tests.runsettings
```

The solution's tests need engine hooks. Keep `TestingEnabled=true` consistent
through restore/build/test; production and test-hook assemblies otherwise share
output paths. Do not run competing builds with different hook settings in the
same checkout. Use a separate worktree for production benchmarks.

For a full solution run, use:

```bash
dotnet test LiteDB.sln -c Release -p:TestingEnabled=true --settings tests.runsettings
```

Executing its Framework targets requires a suitable runtime. Read the actual
project files and [CI workflow](../../.github/workflows/_reusable-ci.yml) for the
current matrix. The library targets `netstandard2.0`, `net8.0`, and `net10.0`;
tests also compile for `net462` and `net481`. A modern-runtime-only test API can
break CI.

For production builds and packages:

```bash
dotnet build LiteDB/LiteDB.csproj -c Release -p:TestingEnabled=false
dotnet pack LiteDB/LiteDB.csproj -c Release -p:TestingEnabled=false -o artifacts_temp
```

Keep test observers, poisoning, and fault-injection hooks behind
`DEBUG || TESTING`, including their call sites and allocations. Production
measurements must use Release with `TestingEnabled=false`.

## Code organization

- Follow nearby C#: four spaces, Allman braces, System imports first; use `var`
  when the type is obvious. Document public APIs and preserve target compatibility.
- Prefer one main class per file and namespaces. Small related helpers may share
  its file. Prefer composed components over splitting a large class into partials
  solely to pass a size check; partials required for generated code are appropriate.
- Aim below 300 lines for new C# files; the limit is 500. Run
  [check-csharp-size.py](../../scripts/check-csharp-size.py) for the changed diff.
  Existing exceptions in [the manifest](../../scripts/csharp-size-exceptions.json)
  have fixed, non-growing limits. Generated-looking names are not exempt.
- Enable the staged check with `git config core.hooksPath .githooks` if needed.
  Preserve existing encoding and avoid unrelated whitespace or line-ending edits.

`LiteDB.Shell/`, `LiteDB.Benchmarks/`, `LiteDB.Stress/`, `LiteDB.Fuzz/`, and
`LiteDB.ReproRunner/` contain the corresponding tools. Keep temporary output in
`artifacts_temp/`, outside the source diff.

## Versioning

GitVersion derives package versions through `Directory.Build.props`; do not
manually bump project versions. Releases use annotated version tags. Preserve
separate GitVersion output files for configuration, target framework, and hook
setting. Worktrees without the `.git/HEAD` sentinel use the detached fallback;
that version does not imply a broken build. See [versioning](../versioning.md).
