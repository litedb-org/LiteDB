import subprocess, pathlib, json, os, time
root = pathlib.Path(os.environ['RUNNER_TEMP'])/'f-p5-macos-cut'
root.mkdir(exist_ok=True)
dll = pathlib.Path('source/LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll').resolve()
results = []
for label, args, budget in [
    ('direct', ['--macos-cut-probe', str(root/'direct')], 360),
    ('replay', ['--replay', str(pathlib.Path('research/f-p5-macos-cut/replay/replay.json').resolve()), '--artifact-dir', str(root/'replay')], 10800),
]:
    started = time.time()
    with (root/(label+'.log')).open('w') as log:
        try:
            p = subprocess.run(['dotnet', str(dll), *args], stdout=log, stderr=subprocess.STDOUT, timeout=budget)
            result = dict(label=label, exit=p.returncode, seconds=time.time()-started, args=args)
        except subprocess.TimeoutExpired:
            result = dict(label=label, timeout=budget, seconds=time.time()-started, args=args)
    results.append(result)
    (root/'commands.json').write_text(json.dumps(results, indent=2))
# A harness failure is retained evidence, never accepted as proof that the cut fired.
if any('timeout' in r or (r['label']=='direct' and r.get('exit')!=0) for r in results):
    raise SystemExit(1)
