import json,subprocess,pathlib,sys
FILES=['contracts.json','fault-points.json','markers.json','coverage-ledger.json','regression-proofs.json']
def obj(rev,file):return json.loads(subprocess.check_output(['git','show',rev+':.github/safety/'+file],text=True))
def dictionaries(o):
 if isinstance(o,dict):
  yield o
  for v in o.values():yield from dictionaries(v)
 if isinstance(o,list):
  for v in o:yield from dictionaries(v)
def records(o,key=''):
 if isinstance(o,dict):
  if o and all(not isinstance(v,(list,dict)) for v in o.values()):
   # These sections are keyed maps: entries may grow or move to a historical
   # map independently. Preserve every key/value, not one immutable whole map.
   if key in {'observers','inheritedObserverDescriptions','models'}:
    for k,v in o.items():yield {k:v}
   else:yield o
  else:
   for k,v in o.items():yield from records(v,k)
 elif isinstance(o,list):
  for v in o:yield from records(v,key)
rows=[]
for target,parents in [('d925be3a6',subprocess.check_output(['git','show','-s','--format=%P','42dc72a4a'],text=True).split()),('7000dd6f0',subprocess.check_output(['git','show','-s','--format=%P','7000dd6f0'],text=True).split())]:
 for parent in parents:
  for f in FILES:
   child=list(dictionaries(obj(target,f)));old=list(records(obj(parent,f)))
   missing=[d for d in old if not any(all(k in c and c[k]==v for k,v in d.items()) for c in child)]
   rows.append({'head':target,'parent':parent,'registry':f,'recordsChecked':len(old),'missingRecords':missing})
assert not any(r['missingRecords'] for r in rows),[r for r in rows if r['missingRecords']]
(pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path(__file__).with_name('registry-record-union.json')).write_text(json.dumps(rows,indent=2)+'\n')
print('PASS:',sum(r['recordsChecked'] for r in rows),'complete scalar-valued records/keyed-map entries across',len(rows),'parent/registry comparisons; none missing.')
