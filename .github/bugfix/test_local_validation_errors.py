"""Local patch defects consume bounded worker retries; network outages do not."""

import hashlib
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from errors import InfrastructureError
import patching
from state import Rejected
from storage import github, run
from test_artifacts import archive
import test_hosted_tick
import test_patching


class LocalValidationErrorTests(unittest.TestCase):
    def test_authenticated_invalid_patch_fails_actual_git_apply_semantically(self):
        root = Path(__file__).resolve().parents[2]
        source = patching.git(root, "rev-parse", "HEAD")
        fixture = test_patching.PatchTests()
        fixture.setUp()
        fixture.state["base_sha"] = source
        fixture.result["base_sha"] = source
        fixture.metadata["base_sha"] = source
        fixture.metadata["result_sha256"] = hashlib.sha256(json.dumps(fixture.result).encode()).hexdigest()
        data = archive({"result.json": fixture.result, "metadata.json": fixture.metadata,
                        "patch.diff": fixture.patch, "runtime-proof.json": fixture.proof})
        real_git = patching.git
        def local_git(repository, *arguments):
            # The immutable commit is already in this local object store. All patch
            # validation and worktree operations below use actual Git commands.
            return "" if arguments[0] == "fetch" else real_git(repository, *arguments)
        with patch("patching.git", side_effect=local_git):
            with self.assertRaises(Rejected) as caught:
                patching.create_candidate(root, root, "owner/repo", fixture.state, source, 123, data)
        self.assertNotIsInstance(caught.exception, InfrastructureError)
        self.assertIn("No valid patches", str(caught.exception))

    def test_local_validation_failure_exhausts_worker_evidence_budget(self):
        campaign = test_hosted_tick.TickCampaignTests().campaign()
        with patch("campaign.repair_feedback", return_value=""), patch("campaign.select_artifact", return_value={}), \
                patch("campaign.download", return_value=b"authenticated-invalid-patch"), \
                patch("campaign.create_candidate", side_effect=Rejected("git apply --check rejected invalid patch")), \
                patch("campaign.publish_candidate") as publish:
            for _ in range(3):
                campaign.repair()
        self.assertEqual(3, campaign.state["orchestration"]["worker_retries"])
        self.assertEqual("block", campaign.record.call_args.args[0]["kind"])
        publish.assert_not_called()

    def test_only_explicit_remote_commands_are_infrastructure(self):
        failure = SimpleNamespace(returncode=1, stderr="failure", stdout="")
        with patch("storage.subprocess.run", return_value=failure):
            for command in (["python", "gate.py", "protect"], ["python", "check-csharp-size.py"]):
                with self.assertRaises(Rejected) as caught:
                    run(command)
                self.assertNotIsInstance(caught.exception, InfrastructureError)
            with self.assertRaises(Rejected) as caught:
                patching.git("repo", "apply", "--check", "patch.diff")
            self.assertNotIsInstance(caught.exception, InfrastructureError)
            for operation in ("fetch", "push", "ls-remote"):
                with self.subTest(operation=operation), self.assertRaises(InfrastructureError):
                    patching.git("repo", operation, "origin")
            with self.assertRaises(InfrastructureError):
                github("owner/repo", "actions/runs/123")


if __name__ == "__main__":
    unittest.main()
