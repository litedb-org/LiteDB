import importlib.util
import json
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('harness_campaign', pathlib.Path(__file__).parent / 'p5/harness_campaign.py')
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)


class HarnessCampaignTests(unittest.TestCase):
    def validate(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for i, row in enumerate(rows):
                child = root / str(i)
                child.mkdir()
                (child / 'run.json').write_text(json.dumps(row))
            return campaign.validate_results(root, 'snapshot', 529470, 'candidate')

    def result(self, **changes):
        return dict(target='snapshot', seed=529470, count=400, status='passed',
                    steps=400, gitSha='candidate', inputHash='input', traceHash='trace', **changes)

    def test_requires_original_and_replay(self):
        with self.assertRaisesRegex(RuntimeError, 'Missing'):
            self.validate([self.result()])

    def test_full_matching_replay_passes(self):
        self.assertEqual(2, len(self.validate([self.result(), self.result()])))

    def test_rejects_partial_failed_wrong_revision_and_changed_replay(self):
        for key, value in [('steps', 348), ('status', 'failed'), ('gitSha', 'old'),
                           ('inputHash', 'changed'), ('traceHash', 'changed')]:
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                row = self.result()
                row[key] = value
                self.validate([self.result(), row])
