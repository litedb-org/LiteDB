"""Scaffold a ReproRunner repro plus its regression-proof entry for a bug fix.

The generated repro pins the known-bad LiteDB in its package variant and its
Program.cs throws until the reproduction is written, so an unfinished scaffold
fails the Regression proof instead of passing it.
"""
import json
import re
from pathlib import Path

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseProjectReference Condition="'$(UseProjectReference)' == ''">false</UseProjectReference>
    <!-- The known-bad state (see .github/safety/regression-proofs.json). -->
    <LiteDBPackageVersion Condition="'$(LiteDBPackageVersion)' == ''">{version}</LiteDBPackageVersion>
  </PropertyGroup>

  <ItemGroup Condition="'$(UseProjectReference)' == 'true'">
    <ProjectReference Include="..\\..\\..\\LiteDB\\LiteDB.csproj" />
  </ItemGroup>

  <ItemGroup Condition="'$(UseProjectReference)' != 'true'">
    <PackageReference Include="LiteDB" Version="$(LiteDBPackageVersion)" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\\..\\LiteDB.ReproRunner.Shared\\LiteDB.ReproRunner.Shared.csproj" />
  </ItemGroup>

  <ItemGroup>
    <AssemblyMetadata Include="LiteDB.ReproRunner.UseProjectReference" Value="$(UseProjectReference)" />
    <AssemblyMetadata Include="LiteDB.ReproRunner.LiteDBPackageVersion" Value="$(LiteDBPackageVersion)" />
  </ItemGroup>
</Project>
"""

PROGRAM = """using System;
using System.IO;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace {id};

/// <summary>
/// Repro of LiteDB issue #{issue}. Exit code 0 means the bug reproduced, which the known-bad
/// LiteDB pinned in the .csproj must do; any other exit code means it did not, which the fixed
/// source must do. Catch the bug's own exception inside <see cref="Reproduce"/>: an escaping
/// exception counts as "did not reproduce".
/// </summary>
internal static class Program
{{
    private static int Main()
    {{
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = context.SharedDatabaseRoot
            ?? Path.Combine(Path.GetTempPath(), "{id}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {{
            var reproduced = Reproduce(Path.Combine(directory, "repro.db"));
            host.SendResult(reproduced, reproduced ? "The bug reproduced." : "The bug did not reproduce.");
            return reproduced ? 0 : 1;
        }}
        catch (Exception error)
        {{
            host.SendResult(false, "The repro failed.", new {{ Exception = error.ToString() }});
            Console.Error.WriteLine(error);
            return 1;
        }}
    }}

    /// <summary>Returns true only when the wrong behavior of issue #{issue} is observed.</summary>
    private static bool Reproduce(string databasePath)
    {{
        using var db = new LiteDatabase(databasePath);
        // TODO: drive the scenario of issue #{issue} and return true when the bug shows.
        throw new NotImplementedException("Write the reproduction for issue #{issue}.");
    }}
}}
"""

README = """# Issue {issue}: {title}

Reproduces [LiteDB issue #{issue}](https://github.com/litedb-org/LiteDB/issues/{issue}).

## Expected outcome

Against the known-bad LiteDB `{version}` pinned in the `.csproj` the repro exits `0` (the bug
reproduces). Against the fixed in-repo source it exits non-zero. The **Regression proof** workflow
requires both; see `docs/rules/safety-evidence.md#regression-proofs`.

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run {id}
```
"""


def create(root, repro_id, issue, title, bad, version, guard):
    """Write the repro folder and return the ledger entry to add."""
    if not re.fullmatch(r"Issue_\d+_[A-Za-z0-9_]+", repro_id):
        raise SystemExit("--id must look like Issue_1234_ShortName")
    folder = Path(root) / "LiteDB.ReproRunner" / "Repros" / repro_id
    if folder.exists():
        raise SystemExit(f"{folder} already exists")
    folder.mkdir(parents=True)
    manifest = {"id": repro_id, "title": title, "issues": [f"https://github.com/litedb-org/LiteDB/issues/{issue}"],
                "failingSince": version, "timeoutSeconds": 120, "requiresParallel": False,
                "defaultInstances": 1, "args": [], "tags": ["regression-proof"], "state": "green"}
    values = {"id": repro_id, "issue": issue, "title": title, "version": version}
    (folder / f"{repro_id}.csproj").write_text(CSPROJ.format(**values), encoding="utf-8")
    (folder / "Program.cs").write_text(PROGRAM.format(**values), encoding="utf-8")
    (folder / "README.md").write_text(README.format(**values), encoding="utf-8")
    (folder / "repro.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return {"repro": repro_id, "knownBad": bad, "permanentGuard": list(guard)}
