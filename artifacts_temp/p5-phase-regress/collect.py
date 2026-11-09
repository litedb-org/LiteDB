import json, subprocess, csv
from pathlib import Path
root=Path(__file__).parent
rows=[]
for run in [42541251747,42541435160,42550856850,42544497136,42547323587,42548192227]:
 state=json.loads(subprocess.check_output(['gh','run','view',str(run),'-R','litedb-org/LiteDB','--json','status,conclusion,headSha,jobs,url'],text=True))
 (root/f'{run}-state.json').write_text(json.dumps(state,indent=2))
 print(run,state['status'],{s:sum(j['status']==s for j in state['jobs']) for s in ['queued','in_progress','completed']})
 for job in state['jobs']:
  if job['conclusion']=='failure':
   log=root/f"job-{job['databaseId']}-failure.log"
   if not log.exists():
    log.write_bytes(subprocess.check_output(['gh','api',f"repos/litedb-org/LiteDB/actions/jobs/{job['databaseId']}/logs"]))
 artifacts=json.loads(subprocess.check_output(['gh','api',f'repos/litedb-org/LiteDB/actions/runs/{run}/artifacts'],text=True))
 for artifact in artifacts['artifacts']:
  dest=root/str(run)/artifact['name']
  if not dest.exists():
   subprocess.run(['gh','run','download',str(run),'-R','litedb-org/LiteDB','-n',artifact['name'],'-D',str(dest)],check=True)
  gate=dest/'gate.json'
  if not gate.exists():
   print('PROFILE' if (dest/'profile-identities.json').exists() else 'NO GATE',artifact['name'], 'traces',len(list(dest.glob('*.nettrace'))));continue
  report=json.loads(gate.read_text());env=json.loads((dest/'environment.json').read_text())
  for row in report['comparisons']:
   rows.append(dict(run=run,leg=artifact['name'],before=env['baselineCommit'],after=env['candidateCommit'],**{k:row[k] for k in ['os','framework','scenario','config','ratio','ratioLowerBound','ratioUpperBound']},beforeMs=row['baseline']['medianMs'],afterMs=row['candidate']['medianMs']))
if rows:
 with (root/'comparisons.csv').open('w') as f:
  w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
 (root/'comparisons.json').write_text(json.dumps(rows,indent=2))
 for r in rows:
  if r['config']=='dev-default':print(r['leg'],r['scenario'],round(r['ratio'],4))
