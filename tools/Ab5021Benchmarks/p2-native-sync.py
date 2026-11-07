"""CI-only native sync counts, with explicit per-commit syscall boundaries."""
import json, os, pathlib, subprocess, sys
assert os.environ.get('GITHUB_ACTIONS') == 'true'
framework = sys.argv[1]
root = pathlib.Path.cwd()
out = root / 'artifacts_temp/p2-profile'
out.mkdir(parents=True, exist_ok=True)
source = r'''using System;
using System.IO;
using LiteDB;
class Program {
 static void Main(string[] args) {
  var path = Path.Combine(Path.GetTempPath(), "p2-sync-" + Guid.NewGuid() + ".db");
  using (var db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct, DurableCommits = args[0] == "default" })) {
   db.CheckpointSize = 0;
   var col = db.GetCollection<BsonDocument>("docs");
   col.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "setup" });
   db.Checkpoint();
   for (int i = 2; i <= 11; ++i) {
    Console.Error.WriteLine("COMMIT_BEGIN:" + i);
    col.Insert(new BsonDocument { ["_id"] = i, ["value"] = "probe" });
    Console.Error.WriteLine("COMMIT_END:" + i);
   }
   if (col.Count() != 11) throw new Exception("count");
  }
  using (var db = new LiteDatabase(path)) if (db.GetCollection("docs").Count() != 11) throw new Exception("reopen");
  foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + "*")) File.Delete(file);
 }
}'''
rows = []
for variant in ['before', 'after']:
 project = root / f'artifacts_temp/sync-probe-{variant}'
 project.mkdir(parents=True, exist_ok=True)
 candidate = root / f'artifacts_temp/{variant}/LiteDB/LiteDB.csproj'
 (project / 'Probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{framework}</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Program.cs"/><ProjectReference Include="{candidate}"/></ItemGroup></Project>')
 (project / 'Program.cs').write_text(source)
 subprocess.run(['dotnet','build',str(project / 'Probe.csproj'),'-c','Release','-p:TestingEnabled=false','-p:GitVersionEnabled=false','--nologo'],check=True)
 for config in ['os-cache','default']:
  trace = out / f'native-{variant}-{config}.strace'
  with (out / f'native-{variant}-{config}.log').open('w') as log:
   subprocess.run(['strace','-f','-yy','-s','200','-e','trace=fsync,fdatasync,syncfs,write','-o',str(trace),'dotnet',str(project / f'bin/Release/{framework}/Probe.dll'),config],stdout=log,stderr=subprocess.STDOUT,check=True,timeout=120)
  counts = []; active = None
  for line in trace.read_text().splitlines():
   if 'write(2' in line and 'COMMIT_BEGIN:' in line: active = 0
   elif 'write(2' in line and 'COMMIT_END:' in line:
    assert active is not None
    counts.append(active); active = None
   elif active is not None and any(x in line for x in ['fsync(', 'fdatasync(', 'syncfs(']): active += 1
  assert len(counts) == 10, counts
  rows.append(dict(variant=variant,config=config,framework=framework,syncsPerCommit=counts))
(out / 'native-sync-counts.json').write_text(json.dumps(rows,indent=2))
print(json.dumps(rows))
