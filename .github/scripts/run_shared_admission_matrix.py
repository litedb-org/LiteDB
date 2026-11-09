"""Run every mapped-selection class in its own bounded VSTest session."""
import re
import subprocess
import sys
from pathlib import Path

framework = sys.argv[1]
selection = "FullyQualifiedName~SharedOneFile|FullyQualifiedName~SharedMapped|FullyQualifiedName~SharedCoordination|FullyQualifiedName~NativeAdmission|FullyQualifiedName~SharedRequestAdmission"
common = ["dotnet", "test", "LiteDB.Tests/LiteDB.Tests.csproj", "-c", "Release", "-f", framework,
          "-p:TestingEnabled=true", "-p:GitVersionEnabled=false", "--no-build", "--no-restore"]
subprocess.run(["dotnet", "build", "LiteDB.Tests/LiteDB.Tests.csproj", "-c", "Release", "-f", framework,
                "-p:TestingEnabled=true", "-p:GitVersionEnabled=false"], check=True)
discovered = subprocess.run(common + ["--list-tests", "--filter", selection], check=True,
                            text=True, stdout=subprocess.PIPE).stdout
Path("artifacts_temp/admission").mkdir(parents=True, exist_ok=True)
Path("artifacts_temp/admission/" + framework + "-discovery.txt").write_text(discovered)
names = re.findall(r"(?m)^\s+(LiteDB\.[A-Za-z0-9_.+]+)(?:\(|\s*$)", discovered)
classes = {name.rsplit(".", 1)[0] for name in names}
assert classes, "No mapped classes discovered by the actual test host"
print("Selected classes:", len(classes), "discovered cases:", len(names), flush=True)
failed = []
for name in sorted(classes):
    print("Mapped session:", framework, name, flush=True)
    result = subprocess.run(["dotnet", "test", "LiteDB.Tests/LiteDB.Tests.csproj", "-c", "Release", "-f", framework,
                             "-p:TestingEnabled=true", "-p:GitVersionEnabled=false", "--no-build", "--no-restore",
                             "--settings", "tests.runsettings", "--filter", "(" + selection + ")&FullyQualifiedName~" + name + ".",
                             "--logger", "trx;LogFileName=" + framework + "-" + name + ".trx",
                             "--results-directory", "artifacts_temp/admission"])
    if result.returncode:
        failed.append(name)
if failed:
    raise SystemExit("Mapped sessions failed: " + ", ".join(failed))
