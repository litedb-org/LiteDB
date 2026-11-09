import json, collections
from pathlib import Path
root=Path(__file__).parent
for path in sorted(root.glob('**/*.speedscope.json')):
 data=json.loads(path.read_text(encoding='utf-8-sig'));frames=data['shared']['frames'];inclusive=collections.Counter();exclusive=collections.Counter();total=0
 def add(stack,weight):
  global total
  if not stack or weight<=0:return
  total+=weight
  for i in set(stack):inclusive[frames[i]['name']]+=weight
  exclusive[frames[stack[-1]]['name']]+=weight
 for profile in data['profiles']:
  if profile['type']=='sampled':
   for sample,weight in zip(profile['samples'],profile.get('weights',[1]*len(profile['samples']))):add(sample,weight)
  elif profile['type']=='evented':
   stack=[];prev=profile['startValue']
   for event in profile['events']:
    add(stack,event['at']-prev);prev=event['at']
    if event['type']=='O':stack.append(event['frame'])
    else:stack.pop()
 out=[f'{path.relative_to(root)} total={total:.4f}', 'Inclusive LiteDB / runtime wait frames:']
 for name,weight in inclusive.most_common():
  if any(x in name for x in ['LiteDB','Monitor','Wait','CanonicalPath','CreateFileW','GetFinalPathName','PulseAll']):out.append(f'{100*weight/total:8.3f}% {weight:.4f} {name}')
 out.append('Exclusive top:')
 out.extend(f'{100*weight/total:8.3f}% {weight:.4f} {name}' for name,weight in exclusive.most_common(35))
 path.with_suffix('.analysis.txt').write_text('\n'.join(out))
 print('\n'.join(out[:30]))
