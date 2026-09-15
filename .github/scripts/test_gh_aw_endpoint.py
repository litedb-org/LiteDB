#!/usr/bin/env python3
"""Exercise endpoint routing and privacy without contacting an API."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import patch_gh_aw_codex_endpoint as patcher
import redact_gh_aw_codex_artifacts as redactor


class EndpointTests(unittest.TestCase):
    def run_runtime_patch(self, endpoint: str, config: dict) -> tuple:
        config.setdefault("apiProxy", {}).setdefault("maxAiCredits", 500)
        with tempfile.TemporaryDirectory() as temp:
            config_path = Path(temp) / "gh-aw" / "awf-config.json"
            config_path.parent.mkdir()
            config_path.write_text(json.dumps(config), encoding="utf-8")
            script = "\n".join(patcher.PATCH_SNIPPET[2:-1])
            result = subprocess.run(
                [sys.executable, "-c", script],
                env={**os.environ, "RUNNER_TEMP": temp, "CODEX_LB_BASE_URL": endpoint},
                capture_output=True,
                text=True,
                check=False,
            )
            return result, json.loads(config_path.read_text(encoding="utf-8"))

    def test_runtime_routes_base_path_without_replacing_other_targets(self):
        result, config = self.run_runtime_patch(
            "https://provider.example.test:8443/backend-api/codex/",
            {"network": {"allowDomains": ["github.com"]},
             "apiProxy": {"targets": {"anthropic": {"host": "other.example.test"}}}},
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            {"host": "provider.example.test:8443", "basePath": "/backend-api/codex"},
            config["apiProxy"]["targets"]["openai"],
        )
        self.assertEqual("other.example.test", config["apiProxy"]["targets"]["anthropic"]["host"])
        self.assertEqual(["github.com", "provider.example.test"], config["network"]["allowDomains"])

    def test_runtime_rejects_ambiguous_or_unsafe_urls(self):
        for endpoint in (
            "", "http://provider.example.test", "https://user:pass@provider.example.test",
            "https://provider.example.test?key=value", "https://provider.example.test#fragment",
        ):
            with self.subTest(endpoint=endpoint):
                result, config = self.run_runtime_patch(endpoint, {})
                self.assertNotEqual(0, result.returncode)
                self.assertEqual({"apiProxy": {"maxAiCredits": 500}}, config)

    def test_runtime_removes_stale_base_path(self):
        result, config = self.run_runtime_patch(
            "https://provider.example.test",
            {"apiProxy": {"targets": {"openai": {"basePath": "/old"}}}},
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("basePath", config["apiProxy"]["targets"]["openai"])

    def test_unknown_models_keep_positive_accounting_and_budget(self):
        result, config = self.run_runtime_patch("https://provider.example.test", {})
        self.assertEqual(0, result.returncode, result.stderr)
        proxy = config["apiProxy"]
        self.assertEqual(500, proxy["maxAiCredits"])
        self.assertEqual({"enabled": False}, proxy["modelFallback"])
        self.assertEqual({"input": 25, "output": 150, "cachedInput": 25, "cacheWrite": 25}, proxy["defaultAiCreditsPricing"])
        for budget in (0, -1):
            result, _ = self.run_runtime_patch("https://provider.example.test", {"apiProxy": {"maxAiCredits": budget}})
            self.assertNotEqual(0, result.returncode)

    def test_lock_patch_is_idempotent_and_fails_closed_on_layout_changes(self):
        fixture = "\n".join([
            "# codex_harness.cjs",
            "      - name: Agent",
            "        run: |",
            '          echo \'{"schema":"awf-config.schema.json"}\' > "${RUNNER_TEMP}/gh-aw/awf-config.json"',
            '          cat > "/tmp/gh-aw/mcp-config/config.toml" << GH_AW_CODEX_SHELL_POLICY_123',
            "          GH_AW_CODEX_SHELL_POLICY_123",
            "          sudo -E awf --config config.json --env-all -- command",
            "        env:",
            "          OPENAI_API_KEY: token-placeholder",
            "      - name: Upload threat detection log",
            "        run: upload",
            "",
        ])
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "worker.lock.yml"
            path.write_text(fixture, encoding="utf-8")
            self.assertTrue(patcher.patch_lockfile(path))
            first = path.read_bytes()
            self.assertFalse(patcher.patch_lockfile(path))
            self.assertEqual(first, path.read_bytes())
            text = path.read_text(encoding="utf-8")
            self.assertIn("--exclude-env CODEX_LB_BASE_URL", text)
            self.assertIn("--legacy-security", text)
            self.assertEqual(2, text.count("CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}"))
            self.assertIn(patcher.DETECTION_REDACTION_MARKER, text)
            path.write_text("# codex_harness.cjs\n", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                patcher.patch_lockfile(path)

    def test_artifact_redaction_removes_endpoint_host_and_port(self):
        endpoint = "https://provider.example.test:8443/backend-api/codex"
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "trace.log"
            path.write_text(f"{endpoint}/responses provider.example.test:8443 provider.example.test", encoding="utf-8")
            self.assertEqual(1, redactor.redact_tree(Path(temp), redactor.redaction_needles(endpoint)))
            self.assertNotIn("provider.example.test", path.read_text(encoding="utf-8"))
            self.assertEqual(0, redactor.redact_tree(Path(temp), redactor.redaction_needles(endpoint)))


if __name__ == "__main__":
    unittest.main()
