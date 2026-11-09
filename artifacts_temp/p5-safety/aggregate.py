import collections,json,pathlib,subprocess,sys,xml.etree.ElementTree as ET
base=pathlib.Path('/tmp/litedb-proto/phases/p5-safety-campaigns-evidence'); artifacts=base/'artifacts'
rows=collections.defaultdict(list); failures=[]; hashes={}; testrows=[]
for p in artifacts.rglob('run.json'):
 try:d=json.loads(p.read_text())
 except Exception:continue
 if 'target' not in d:continue
 platform=next((v for v in p.relative_to(artifacts).parts if v.startswith('p5-')),'unknown')
 kind='replay' if 'replays' in p.parts else 'campaign'
 rows[(d['target'],platform,kind)].append(d)
 if d['status']!='passed': failures.append(dict(path=str(p),**d))
for p in artifacts.rglob('binary-sha256.json'): hashes[str(p)]=json.loads(p.read_text())
(base/'all-binary-sha256.json').write_text(json.dumps(hashes,indent=2))
(base/'all-failures.json').write_text(json.dumps(failures,indent=2))
for p in artifacts.rglob('*.trx'):
 try:
  r=ET.parse(p).getroot();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};c=r.find('.//t:Counters',ns)
  testrows.append(dict(file=str(p),**(c.attrib if c is not None else {})))
 except Exception as e:testrows.append(dict(file=str(p),error=str(e)))
(base/'trx-counts.json').write_text(json.dumps(testrows,indent=2))
lines=['# P5 safety qualification','', 'Candidate: `e507faad40e968aa2fcf63ae6dd01fd7def2e07b`. Driver: `v6/wip-p5-campaigns@b811372fb09ea469ea884d06df22002225df4d71`.','', 'Run: https://github.com/litedb-org/LiteDB/actions/runs/42531054661. All product and harness binaries build from the exact candidate checkout; the scratch driver is external. No product changes.','', '| Target | Platform/job | Kind | Seeds | Steps | Wall seconds (sum) | Failures | Reachability |','| --- | --- | --- | ---: | ---: | ---: | ---: | --- |']
for (target,platform,kind),ds in sorted(rows.items()):
 lines.append(f"| {target} | {platform} | {kind} | {','.join(str(v) for v in sorted(set(d['seed'] for d in ds)))} | {sum(d['steps'] for d in ds)} | {sum(d['durationSeconds'] for d in ds):.3f} | {sum(d['status']!='passed' for d in ds)} | see reachability.json |")
lines+=['','Binary SHA256: [all-binary-sha256.json](p5-safety-campaigns-evidence/all-binary-sha256.json); exact binaries, recorded inputs, run metrics, traces and replay files retained in hosted artifacts and evidence/artifacts.','',f"Completed fuzz steps (including corpus, excluding replays): {sum(d['steps'] for (t,p,k),ds in rows.items() if k=='campaign' for d in ds)}.",f'Fuzz failures including replays: {len(failures)}; see all-failures.json. Fault-test counts: trx-counts.json.','','Residual risks: finite generated schedules and fault scenarios do not cover all execution interleavings or hardware failures. Process death retains OS cache; modeled power loss covers only durability semantics encoded by the test streams. No physical device, VM or OS reset was performed. Network filesystems, cross-host locking, server/client cache coherence and distributed durability are unqualified. Unsynced directory-entry loss and reader lease publication/renewal/expiry hook gaps remain inherited limits. shared-wake is Linux-specific and returns without steps on Windows/macOS; portable Shared and handle targets still run there.','', 'State: RUNNING until terminal run and complete replay/reachability audit.']
pathlib.Path('/tmp/litedb-proto/phases/p5-safety-report.md').write_text('\n'.join(lines)+'\n')
print('campaign groups',len(rows),'steps',sum(d['steps'] for (t,p,k),ds in rows.items() if k=='campaign' for d in ds),'failures',len(failures),'TRX',len(testrows))
