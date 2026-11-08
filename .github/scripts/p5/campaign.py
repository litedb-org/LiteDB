"""Dispatch-only external driver; candidate sources are never modified."""
import argparse, hashlib, json, os, pathlib, re, subprocess, time
p=argparse.ArgumentParser(); p.add_argument('mode'); p.add_argument('--targets',default=''); a=p.parse_args()
root=pathlib.Path('evidence'); root.mkdir(exist_ok=True)
sha=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
assert sha=='e507faad40e968aa2fcf63ae6dd01fd7def2e07b',sha
binary=pathlib.Path('LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll')
cmd=['dotnet',str(binary.resolve())]
records=[]
def run(args, name):
    start=time.time()
    with (root/(name+'.log')).open('w',encoding='utf-8') as log:
        proc=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT)
    row=dict(name=name,command=args,exit=proc.returncode,seconds=time.time()-start)
    records.append(row); (root/'commands.json').write_text(json.dumps(records,indent=2))
    print(json.dumps(row),flush=True)
    return proc.returncode
if a.mode=='fuzz':
    # Maximum standard deterministic target budget is 40. Each seed gets 10x;
    # ten independent seeds also increase the normal seed budget by 10x.
    for target in a.targets.split(','):
        for seed in range(529470,529480):
            dest=root/'fuzz'/target/str(seed)
            run(cmd+['--target',target,'--seed',str(seed),'--count','400','--artifact-dir',str(dest),'--max-artifact-mb','0'],f'{target}-{seed}')
    failures=[]
    for result in list((root/'fuzz').rglob('run.json')):
        data=json.loads(result.read_text())
        if data['status']=='failed':
            replay=result.with_name('minimized-replay.json')
            if not replay.exists(): replay=result.with_name('replay.json')
            replaydir=root/'replays'/result.parent.name
            code=run(cmd+['--replay',str(replay),'--artifact-dir',str(replaydir),'--max-artifact-mb','0'],'replay-'+result.parent.name)
            failures.append(dict(original=str(result),replay=str(replay),exit=code))
    (root/'replay-index.json').write_text(json.dumps(failures,indent=2))
elif a.mode=='faults':
    # Run every concrete test class referenced by the fault registry, plus the
    # complete existing power-loss, process-crash, and transaction-handle suites.
    reg=pathlib.Path('.github/safety/fault-points.json').read_text()
    files=set(re.findall(r'"test":\s*"([^"#]+)#',reg))
    for path in pathlib.Path('LiteDB.Tests').rglob('*_Tests.cs'):
        if any(x.lower() in path.name.lower() for x in ['PowerLoss','Crash','TransactionHandle','FinalClose','Maintenance']): files.add(str(path))
    classes=set()
    for file in files:
        for cls in re.findall(r'public\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)',pathlib.Path(file).read_text(encoding='utf-8-sig')): classes.add(cls)
    (root/'fault-classes.json').write_text(json.dumps(sorted(classes),indent=2))
    for cls in sorted(classes):
        args=['dotnet','test','LiteDB.Tests/LiteDB.Tests.csproj','-c','Release','-f','net8.0','--no-build','-p:TestingEnabled=true','-p:GitVersionEnabled=false','--settings','tests.runsettings','--filter','FullyQualifiedName~'+cls+'.','--logger','trx;LogFileName='+cls+'.trx','--results-directory',str(root/'trx')]
        if run(args,cls): run(args,cls+'-replay')
# Hash actual binaries after final build and all test executions.
hashes={str(f):hashlib.sha256(f.read_bytes()).hexdigest() for base in ['LiteDB.Fuzz/bin/Release/net8.0','LiteDB.Tests/bin/Release/net8.0'] for f in pathlib.Path(base).glob('*') if f.is_file() and f.suffix in ['.dll','.exe','.json']}
(root/'binary-sha256.json').write_text(json.dumps(hashes,indent=2))
(root/'candidate.txt').write_text(sha+'\n')
raise SystemExit(1 if any(r['exit'] for r in records) else 0)
