import json,collections,statistics
from pathlib import Path
root=Path(__file__).parent
rows=json.loads((root/'comparisons.json').read_text())
envelope=json.loads((root/'historical-aa-envelope.json').read_text())
groups=collections.defaultdict(list)
for r in rows:
 groups[r['leg'].split('-r')[0],r['os'],r['framework'],r['scenario'],r['config']].append(r)
lines=['# Completed paired timing evidence (incremental)', '', 'Ratios after/before from independent hosted jobs; each job retains six interleaved rounds. Report-only subsets do not issue a release gate verdict.', '', '|Probe|OS/runtime|Scenario/config|Ratios by repetition|Historical A/A upper (advisory)|Extra allocated bytes (median across jobs)|','|---|---|---|---|---:|---:|']
for key,rs in sorted(groups.items()):
 allocations=[]
 for r in rs:
  folder=root/str(r['run'])/r['leg']
  b=json.loads((folder/'before/summary.json').read_text());a=json.loads((folder/'summary.json').read_text())
  pair=[next(t for t in side if t['scenario']==r['scenario'] and t['config']==r['config']) for side in [b,a]]
  allocations.append(pair[1]['allocatedBytes']['median']-pair[0]['allocatedBytes']['median'])
 probe,os,fw,sc,config=key
 lines.append(f"|{probe}|{os} {fw}|{sc}/{config}|"+', '.join(f"{r['ratio']:.4f}" for r in sorted(rs,key=lambda r:r['leg']))+f"|{envelope.get('/'.join([os,fw,sc,config]), 'N/A')}|{statistics.median(allocations):g}|")
(root/'paired-timings.md').write_text('\n'.join(lines)+'\n')
print('\n'.join(lines))
