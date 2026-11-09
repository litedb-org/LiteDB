import collections,datetime,json,pathlib,subprocess,time,zipfile,requests,traceback
b=pathlib.Path('/tmp/litedb-proto/phases/p5-safety-campaigns-evidence');repo='litedb-org/LiteDB';run='42531054661';seen=set()
for p in (b/'artifacts').iterdir():
 if (p/'evidence/binary-sha256.json').exists():seen.add(p.name)
while True:
 try:
  raw=subprocess.check_output(['gh','run','view',run,'-R',repo,'--json','status,conclusion,jobs'],text=True);(b/'run.json').write_text(raw);d=json.loads(raw);now=datetime.datetime.now(datetime.timezone.utc).isoformat();counts=dict(collections.Counter(j['status']+':'+j['conclusion'] for j in d['jobs']));print(now,counts,flush=True)
  with pathlib.Path('/tmp/litedb-proto/phases/p5-safety-campaigns.md').open('a') as f:f.write('- '+now+' hosted '+run+': '+str(counts)+'; streaming collector active.\n')
  arts=json.loads(subprocess.check_output(['gh','api',f'repos/{repo}/actions/runs/{run}/artifacts?per_page=100'],text=True));(b/'artifacts-index.json').write_text(json.dumps(arts,indent=2))
  for a in arts['artifacts']:
   name=a['name']
   if name in seen:continue
   dest=b/'artifacts'/name;dest.mkdir(exist_ok=True);archive=b/(name+'.zip');print('DOWNLOAD',name,a['size_in_bytes'],flush=True)
   token=subprocess.check_output(['gh','auth','token'],text=True).strip()
   with requests.get(a['archive_download_url'],headers={'Authorization':'Bearer '+token},stream=True,timeout=(30,120)) as r:
    r.raise_for_status()
    with archive.open('wb') as f:
     for chunk in r.iter_content(1024*1024):f.write(chunk)
   with zipfile.ZipFile(archive) as z:z.extractall(dest)
   seen.add(name);(dest/'download-complete.txt').write_text(str(a['id']));archive.unlink();print('DOWNLOADED',name,flush=True)
   subprocess.run(['python3',str(b/'aggregate.py')])
  if d['status']=='completed' and len(seen)==len(d['jobs']):
   subprocess.run(['gh','run','view',run,'-R',repo,'--log'],stdout=(b/'hosted.log').open('w'));print('TERMINAL',d['conclusion'],flush=True);break
 except Exception:traceback.print_exc()
 time.sleep(180)
