#!/usr/bin/env python3
"""Confirm that version-only retention misses checkpoint/reset churn; restore protected source afterwards."""
from pathlib import Path
import subprocess,xml.etree.ElementTree as ET
source=Path('candidate/LiteDB/Client/Shared/SharedEngine.CoordinatedReads.cs');current=source.read_bytes()
e=Path('evidence/stability-negative');e.mkdir(parents=True,exist_ok=True)
try:
 source.write_bytes(subprocess.check_output(['git','-C','candidate','show','305f7e61d:LiteDB/Client/Shared/SharedEngine.CoordinatedReads.cs']))
 with (e/'before.log').open('w') as log:
  result=subprocess.run(['dotnet','test','candidate/LiteDB.Tests','-c','Release','-f','net8.0','-p:TestingEnabled=true','--settings','candidate/tests.runsettings','--filter','FullyQualifiedName~Reads_between_peer_commits_do_not_retain_unused_snapshots','--logger','trx','--results-directory',str(e)],stdout=log,stderr=subprocess.STDOUT)
 assert result.returncode==1, result.returncode
 tree=ET.parse(next(e.glob('*.trx')));ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
 failures=[x for x in tree.findall('.//t:UnitTestResult',ns) if x.attrib['outcome']=='Failed']
 assert len(failures)==1
 assert 'changed storage cannot amortize retention' in ''.join(failures[0].itertext())
 print('Version-only negative control failed the checkpoint/reset retention assertion as expected.')
finally:source.write_bytes(current)
