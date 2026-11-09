"""Fast synthetic evidence-audit checks; does not execute a benchmark."""
import copy
import itertools
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('summarize.py')
CANDIDATE = '7000dd6f01d0187a8fb5d030064eb327864ab731'
HARNESS = '1ae1bed23dfd88ab74cafb02a58b3283ccced940'


class SummaryAuditTests(unittest.TestCase):
    def test_complete_evidence_and_reject_incomplete_or_wrong_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            files = []
            identities = {}
            for os_name, version in itertools.product(('Linux', 'Windows', 'Darwin'), (8, 10)):
                runtime = f'.NETCoreApp,Version=v{version}.0'
                architecture = 'Arm64' if os_name == 'Darwin' else 'X64'
                platform = 'macOS ARM64' if os_name == 'Darwin' else f'{os_name} x64'
                identities[f'{platform}|{runtime}'] = dict(harness=HARNESS, readRequests=200 if os_name == 'Darwin' else 20, minimumOverlap=2 if os_name == 'Darwin' else 4)
                rows = []
                for rep, round_id, scenario, workers, variant in itertools.product((1, 2), range(6), ('read', 'insert', 'mixed'), (1, 4), ('v5', 'v6')):
                    rows.append(dict(repetition=rep, round=round_id, scenario=scenario, workers=workers,
                                     variant=variant, verified=True, requestsPerWorker=200 if os_name == 'Darwin' and scenario == 'read' else 20, warmupRequests=10,
                                     latencyMs=2 if variant == 'v6' else 1, wallMsPerRequest=1,
                                     processes=[dict(start='2026-11-09T00:00:00Z', end='2026-11-09T00:00:01Z', runtime=f'.NET {version}.0.0', os=os_name, architecture=architecture, framework=runtime,
                                                     candidateCommit=CANDIDATE, harnessCommit=HARNESS,
                                                     tieredCompilation='0', requests=200 if os_name == 'Darwin' and scenario == 'read' else 20, milliseconds=100,
                                                     assemblySha256=variant) for _ in range(workers)]))
                path = root / f'{os_name}-{runtime}' / 'all.json'
                path.parent.mkdir()
                path.write_text(json.dumps(rows))
                files.append(path)
            manifest = root / 'identities.json'
            manifest.write_text(json.dumps(identities))
            def run():
                return subprocess.run([sys.executable, str(SCRIPT), str(root), str(root / 'report'), str(manifest)], capture_output=True)
            result = run()
            self.assertEqual(result.returncode, 0, result.stderr)
            summary = json.loads((root / 'report' / 'summary.json').read_text())
            self.assertEqual(len(summary), 144)
            self.assertTrue(all(r['ratio'] == 2 for r in summary if r['metric'] == 'latencyMs'))
            original = json.loads(files[0].read_text())
            mutations = [lambda r: r.pop(),
                         lambda r: r[0]['processes'][0].update(candidateCommit='wrong'),
                         lambda r: r[0].update(verified=False),
                         lambda r: r[0]['processes'][0].update(assemblySha256='different')]
            for mutate in mutations:
                rows = copy.deepcopy(original)
                mutate(rows)
                files[0].write_text(json.dumps(rows))
                self.assertNotEqual(run().returncode, 0)
            files[0].write_text(json.dumps(original))


if __name__ == '__main__':
    unittest.main()
