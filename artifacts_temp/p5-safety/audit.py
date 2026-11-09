import collections,hashlib,json,pathlib,subprocess,xml.etree.ElementTree as ET
b=pathlib.Path('/tmp/litedb-proto/phases/p5-safety-campaigns-evidence');art=b/'artifacts'
subprocess.run(['python3','/tmp/litedb-proto/p5-pr3142-source/.github/scripts/check_reachability.py','--head','e507faad4','--runs',str(art),'--output',str(b/'reachability.json')],stdout=(b/'reachability.log').open('w'),check=True)
r=json.loads((b/'reachability.json').read_text());reg=json.loads((b/'candidate-fault-points.json').read_text());testresults=[]
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for p in art.rglob('*.trx'):
 root=ET.parse(p).getroot()
 for x in root.findall('.//t:UnitTestResult',ns):testresults.append(dict(file=str(p.relative_to(b)),name=x.attrib.get('testName',''),outcome=x.attrib['outcome']))
(b/'test-results.json').write_text(json.dumps(testresults,indent=2))
rows=[]
for category in ['hooks','injectors']:
 for e in reg[category]:
  m='fault-point:'+e['name'];hit=r['markers'].get(m,{}).get('hits',0);evidence=[]
  for ev in e.get('evidence',[]):
   if 'test' not in ev:continue
   source,method=ev['test'].split('#');cls=pathlib.Path(source).stem
   matches=[x for x in testresults if cls+'.'+method in x['name']]
   evidence.append(dict(test=ev['test'],results=dict(collections.Counter(x['outcome'] for x in matches)),files=sorted(set(x['file'] for x in matches))))
  rows.append(dict(category=category,name=e['name'],hits=hit,status='reached' if hit else 'unreached (no marker telemetry; xUnit execution listed separately)',testEvidence=evidence,gap=e.get('gap','')))
(b/'fault-audit.json').write_text(json.dumps(rows,indent=2))
lines=['# Registered fault-point audit','','Recorded marker hits establish reachability. xUnit execution is additional evidence, not marker telemetry. Zero-hit entries remain explicitly unreached by telemetry.','','| Kind | Point | Hits | Reachability | Registered test execution (pass/fail/skip) |','| --- | --- | ---: | --- | --- |']
for row in rows:
 counts=collections.Counter()
 for e in row['testEvidence']:counts.update(e['results'])
 lines.append(f"| {row['category']} | {row['name']} | {row['hits']} | {row['status']} | {dict(counts)} |")
lines+=['','## All unreached markers','']+['- `'+n+'`' for n in r['neverHit']]
(b/'reachability-audit.md').write_text('\n'.join(lines)+'\n')
validation=[]
for p in art.rglob('binary-sha256.json'):
 hashes=json.loads(p.read_text());job=p.relative_to(art).parts[0]
 for name,expected in hashes.items():
  basename=name.replace('\\','/').split('/')[-1];matches=list((art/job).rglob(basename));good=[x for x in matches if hashlib.sha256(x.read_bytes()).hexdigest()==expected]
  validation.append(dict(job=job,file=name,sha256=expected,verified=bool(good)))
(b/'hash-validation.json').write_text(json.dumps(validation,indent=2))
print('markers',len(r['markers']),'unreached',len(r['neverHit']),'hooks',len(rows),'unreached hooks',sum(not x['hits'] for x in rows),'hashes',len(validation),'bad',sum(not x['verified'] for x in validation))
