import collections,json,pathlib,subprocess,yaml
b=pathlib.Path('/tmp/litedb-proto/phases/p5-safety-campaigns-evidence');w=yaml.safe_load(subprocess.check_output(['git','show','b811372fb:.github/workflows/fuzz.yml'],text=True));matrix=w['jobs']['campaign']['strategy']['matrix']['include'];rows=[]
for leg in matrix:
 folder=b/'artifacts'/('p5-'+leg['id'])/'evidence';commands=json.loads((folder/'commands.json').read_text()) if (folder/'commands.json').exists() else []
 if leg['mode']=='fuzz':
  runs=[]
  for p in (folder/'fuzz').rglob('run.json'):
   d=json.loads(p.read_text())
   if 'target' in d:runs.append(d)
  for target in leg['targets'].split(','):
   ds=[x for x in runs if x['target']==target];seeds={x['seed'] for x in ds};missing=sorted(set(range(529470,529480))-seeds);rows.append(dict(job=leg['id'],target=target,requestedSeeds=list(range(529470,529480)),missingSeeds=missing,runs=len(ds),steps=sum(x['steps'] for x in ds),failures=sum(x['status']!='passed' for x in ds),shaMismatch=sorted({x.get('gitSha') for x in ds if x.get('gitSha')!='e507faad40e968aa2fcf63ae6dd01fd7def2e07b'})))
 else:rows.append(dict(job=leg['id'],target='registered fault classes',commands=len(commands),failedCommands=sum(x['exit']!=0 for x in commands)))
(b/'inventory-check.json').write_text(json.dumps(rows,indent=2));print('matrix legs',len(matrix),'target jobs',len(rows),'missing seed invocations',sum(len(x.get('missingSeeds',[])) for x in rows),'SHA mismatches',[x for x in rows if x.get('shaMismatch')])
