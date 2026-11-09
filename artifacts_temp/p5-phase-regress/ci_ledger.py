"""Summarize saved hosted run states; no network or product execution."""
import json
from pathlib import Path
root=Path(__file__).parent
purpose={42541251747:'24 adjacent-phase timing legs; obsolete post-timing trace CLI failures',42541435160:'14 introducing/control timing legs; obsolete post-timing trace CLI failures',42544497136:'3 primary diagnostic pairs; exact0b and optional raw-drain production-build failures',42547323587:'2 net8 writer diagnostic pairs',42548192227:'1 integrated Phase3/Phase4 net8 writer diagnostic pair',42550856850:'2 buildable reader activation timing legs and1 diagnostic job (scan +open control)'}
lines=['# Hosted CI evidence ledger','','Workflow success is distinct from valid report-only measurements. Historical failures and superseded runs are retained in the step log; no failed workflow is called green.','','|Run|Purpose|Harness SHA|Status / conclusion|Active matrix jobs: success / failure / queued / running|','|---|---|---|---|---|']
for run,desc in purpose.items():
 d=json.loads((root/f'{run}-state.json').read_text());jobs=[j for j in d['jobs'] if j['name'].startswith('Research ')]
 counts=[sum(j['conclusion']==s for j in jobs) for s in ['success','failure']]+[sum(j['status']==s for j in jobs) for s in ['queued','in_progress']]
 lines.append(f"|[run{run}]({d['url']})|{desc}|{d['headSha']}|{d['status']} / {d['conclusion'] or 'pending'}|{' / '.join(map(str,counts))}|")
lines+=['','All runs use unmodified before/after product commits. Benchmark checksums and graceful reopen are diagnostic integrity checks; no new product safety qualification or fixed-binary gain is claimed. No local benchmarks ran.']
(root/'ci-evidence.md').write_text('\n'.join(lines)+'\n')
