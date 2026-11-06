"""Summarize downloaded paired diagnostics by platform, revision and theory route.

Usage: python analyze.py <directory containing named artifacts>
The xUnit adapter reports receipt timestamps in TRX; reconstruct the case interval
from its end and duration to associate child stderr with the theory route. Exclude
post-timeout completions because stack collection can affect their latency.
"""
from pathlib import Path
import xml.etree.ElementTree as ET,re,json,statistics
from datetime import datetime,timedelta
import sys
rows={}
def date(s): return datetime.fromisoformat(s.replace('Z','+00:00'))
for trx in Path(sys.argv[1]).rglob('results.trx'):
 for case in ET.parse(trx).findall('.//{*}UnitTestResult'):
  end=date(case.attrib['endTime']); h,m,sec=case.attrib['duration'].split(':');start=end-timedelta(hours=int(h),minutes=int(m),seconds=float(sec))
  for child in trx.parent.glob('child-*.txt'):
   text=child.read_text();first=re.search(r'insert-begin \d+ (\S+)',text)
   if not first or not start<=date(first[1])<=end:continue
   timeout=trx.parent/'timeouts.txt';cutoff=date(timeout.read_text().split()[0]) if timeout.exists() and f'child={child.stem.split("-")[-1]}' in timeout.read_text() else end
   for number,stamp,ms in re.findall(r'insert-end (\d+) (\S+) ms=([\d.]+)',text):
    if date(stamp)>cutoff:continue
    key=(trx.parents[2].name,trx.parent.parent.name,case.attrib['testName'].split('(')[-1].rstrip(')'), 'first' if number=='3000' else 'subsequent')
    rows.setdefault(key,[]).append(float(ms))
for key,values in sorted(rows.items()):
 print(json.dumps(dict(platform=key[0],revision=key[1],route=key[2],part=key[3],n=len(values),median_ms=statistics.median(values),p95_ms=sorted(values)[int((len(values)-1)*.95)])))
