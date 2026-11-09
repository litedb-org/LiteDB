import subprocess, pathlib, json, os, time
root = pathlib.Path(os.environ['RUNNER_TEMP'])/'f-p5-macos-cut'
root.mkdir(exist_ok=True)
dll = pathlib.Path('source/LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll').resolve()
results = []
for label, args, budget in [
    ('direct', ['--macos-cut-probe', str(root/'direct')], 360),
    ('replay', ['--replay', str(pathlib.Path('research/f-p5-macos-cut/replay/replay.json').resolve()), '--artifact-dir', str(root/'replay')], 10800),
]:
    if label == "replay" and os.environ.get("DIRECT_ONLY") == "true": continue
    started = time.time()
    with (root/(label+'.log')).open('w') as log:
        try:
            binary = dll if label == 'direct' else root/'original-binary/LiteDB.Fuzz.dll'
            p = subprocess.run(['dotnet', str(binary), *args], stdout=log, stderr=subprocess.STDOUT, timeout=budget)
            result = dict(label=label, exit=p.returncode, seconds=time.time()-started, args=args)
        except subprocess.TimeoutExpired:
            result = dict(label=label, timeout=budget, seconds=time.time()-started, args=args)
    results.append(result)
    (root/'commands.json').write_text(json.dumps(results, indent=2))
# A harness failure is retained evidence, never accepted as proof that the cut fired.
if any('timeout' in r or r.get('exit')!=0 for r in results):
    raise SystemExit(1)

# Require completed replay, full boundary credit, and the reported step's exact cut.
if os.environ.get("DIRECT_ONLY") != "true":
    runs = list((root/'replay').rglob('run.json'))
    assert len(runs) == 1, runs
    run = json.loads(runs[0].read_text())
    assert run['steps'] == 400, run
    for key, value in dict(rounds=400, processes=1200, observedCrashPositions=26, observedInternalCrashPoints=14, integrityChecks=400).items():
        assert run['metrics'][key] == value, (key, run)
    (root/'replay-summary.json').write_text(json.dumps(run, indent=2))
    trace = [json.loads(line) for line in runs[0].with_name('trace.jsonl').read_text().splitlines()]
    exits = [r for r in trace if r.get('step') == 282 and r.get('operation') == 'child-exit' and r.get('detail', {}).get('worker') == 0]
    assert len(exits) == 1 and 'checkpoint-after-page-write' in exits[0]['detail']['error'], exits
