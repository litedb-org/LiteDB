from pathlib import Path
import shutil
source = Path('source')
shutil.copy('research/f-p5-macos-cut/MacosCutProbe.cs', source/'LiteDB.Fuzz/MacosCutProbe.cs')
p = source/'LiteDB.Fuzz/Program.cs'
s = p.read_text().replace('        FuzzOptions options;', '        if (args.Length == 2 && args[0] == "--macos-cut-probe") return await MacosCutProbe.Run(args[1]);\n        FuzzOptions options;')
p.write_text(s)
p = source/'LiteDB.Fuzz/Targets/SharedProcessFuzzer.cs'
s = p.read_text().replace('                if (phase != crashPoint) return;', '                if (options.CrashAt == 22) File.AppendAllText(options.Ledger + ".hooks", phase + "\\n");\n                if (phase != crashPoint) return;')
s = s.replace('            db.Checkpoint();\n            Crash(11', '            var cutCheckpointPages = db.Checkpoint();\n            File.WriteAllText(options.Ledger + ".checkpoint-result", cutCheckpointPages.ToString());\n            Crash(11')
p.write_text(s)
