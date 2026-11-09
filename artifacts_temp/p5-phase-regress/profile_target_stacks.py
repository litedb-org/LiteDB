"""Offline stack attribution only; never executes the measured database."""
import json, collections
from pathlib import Path
root=Path(__file__).parent
for path in sorted(root.glob('**/*.speedscope.json')):
 data=json.loads(path.read_text(encoding='utf-8-sig'));names=[x['name'] for x in data['shared']['frames']]
 if 'durable_insert' in path.name:
  accept=lambda stack:any('DurableWriters+<>c__DisplayClass' in names[i] and '<Run>b__' in names[i] for i in stack)
 elif 'open_close' in path.name:
  accept=lambda stack:any('Ab5021Benchmarks.Workload.Open(' in names[i] for i in stack)
 elif 'full_scan' in path.name:
  accept=lambda stack:any('LiteQueryable`1+<ReadDocuments>' in names[i] for i in stack)
 else:continue
 total=0;inc=collections.Counter()
 def add(stack,weight):
  global total
  if weight<=0 or not accept(stack):return
  total+=weight
  for i in set(stack):inc[names[i]]+=weight
 for profile in data['profiles']:
  if profile['type']=='sampled':
   for sample,weight in zip(profile['samples'],profile.get('weights',[1]*len(profile['samples']))):add(sample,weight)
  else:
   stack=[];prev=profile['startValue']
   for event in profile['events']:
    add(stack,event['at']-prev);prev=event['at']
    if event['type']=='O':stack.append(event['frame'])
    else:stack.pop()
 out=[str(path.relative_to(root)),f'Selected sampled-thread-time weight={total:.4f}; percentages use only target stacks. Not CPU percentages or elapsed ratios.']
 for name,weight in inc.most_common():
  if any(x in name for x in ['CollectionLock','Monitor.Wait','TransactionWaitBudget','WalCommitGroup.EnsureDurable','Monitor.Enter','Monitor.TryEnter','Lock.TryEnter','PulseAll','CanonicalPath','EnsureAvailable','UnboundEngine+Reader','TransactionContext.Unbound','BsonDataReader.Read','ReadDocuments']):
   out.append(f'{100*weight/total if total else 0:8.3f}% {weight:.4f} {name}')
 path.with_suffix('.target.txt').write_text('\n'.join(out)+'\n')
 print('\n'.join(out))
