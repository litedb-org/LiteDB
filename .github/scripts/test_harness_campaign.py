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

    def test_shutdown_reaps_a_child_spawned_after_first_enumeration(self):
        from unittest.mock import Mock, patch
        import types
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            binary = root / 'LiteDB.Fuzz.dll'
            (root / 'reader-1.ready').write_text('ready')
            parent, late_child = Mock(), Mock()
            for process in [parent, late_child]:
                process.info = {'cmdline': ['dotnet', str(binary.resolve())]}
            fake = types.SimpleNamespace(NoSuchProcess=ProcessLookupError, AccessDenied=PermissionError,
                process_iter=Mock(side_effect=[[parent], [late_child], []]),
                wait_procs=Mock(side_effect=lambda processes, timeout: ([], processes)))
            with patch.dict('sys.modules', psutil=fake):
                campaign.shutdown(binary, root)
            self.assertEqual('stop', (root / 'reader-1.stop').read_text())
            for process in [parent, late_child]:
                process.terminate.assert_called_once()
                process.kill.assert_called_once()

    def test_shutdown_refuses_unstable_evidence(self):
        from unittest.mock import Mock, patch
        import types
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            binary = root / 'LiteDB.Fuzz.dll'
            process = Mock()
            process.info = {'cmdline': [str(binary.resolve())]}
            fake = types.SimpleNamespace(NoSuchProcess=ProcessLookupError, AccessDenied=PermissionError,
                process_iter=Mock(return_value=[process]),
                wait_procs=Mock(side_effect=lambda processes, timeout: ([], processes)))
            with patch.dict('sys.modules', psutil=fake), self.assertRaisesRegex(RuntimeError, 'unstable'):
                campaign.shutdown(binary, root)
