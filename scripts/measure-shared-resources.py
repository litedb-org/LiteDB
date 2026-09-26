#!/usr/bin/env python3
"""Production resource diagnostic and independent Direct-mode reference, five alternating pairs."""
import argparse,json,os,subprocess,hashlib
from pathlib import Path
p=argparse.ArgumentParser()
for k in ('baseline','candidate','scratch','output'):p.add_argument('--'+k,type=Path,required=True)
a=p.parse_args();a.scratch.mkdir(parents=True,exist_ok=True);a.output.parent.mkdir(parents=True,exist_ok=True)
env=dict(os.environ,TMPDIR=str(a.scratch.resolve()))
with a.output.open('x') as output:
 for case in ('resources','direct-point','direct-scan'):
  for pair in range(5):
   for variant in (('baseline','candidate') if pair%2==0 else ('candidate','baseline')):
    dll=(getattr(a,variant) if case=='resources' else a.candidate).resolve()/'SharedReadBenchmarks.dll'
    cmd=['dotnet',str(dll)]
    if case=='resources':cmd+=['connection-resources',str(a.scratch.resolve())]
    else:cmd += [str(a.scratch.resolve()),'direct' if variant=='baseline' else 'shared',case.split('-')[1],'20000' if case=='direct-point' else '1000','10']
    r=subprocess.run(cmd,env=env,text=True,capture_output=True,timeout=290)
    record=dict(case=case,pair=pair,variant=variant,command=cmd,returncode=r.returncode,stderr=r.stderr,librarySha256=hashlib.sha256((dll.parent/'LiteDB.dll').read_bytes()).hexdigest())
    if r.returncode:record['stdout']=r.stdout
    else:record['result']=json.loads(r.stdout)
    output.write(json.dumps(record)+'\n');output.flush()
    if r.returncode:raise SystemExit('Diagnostic failed; evidence preserved')
