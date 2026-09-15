#!/usr/bin/env python3
"""Verify pinned Codex reasoning requests against a local HTTP stub, without inference."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


CODEX_VERSION = "0.154.0"
MODELS = ("gpt-6-astra", "gpt-5.6-sol")
REASONING_OVERRIDES = (
    "model_reasoning_effort=high",
)


def capture_request(executable: str, model: str) -> dict:
    observations = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 1048576:
                self.send_error(413)
                return
            payload = json.loads(self.rfile.read(length))
            reasoning = payload.get("reasoning") or {}
            observations.append({"model": payload.get("model"), "reasoning_effort": reasoning.get("effort")})
            body = b'{"error":{"message":"Offline request capture complete","type":"invalid_request_error"}}'
            self.send_response(400)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    with tempfile.TemporaryDirectory(prefix="codex-reasoning-probe-") as temporary:
        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            settings = [
                f'model="{model}"',
                'model_provider="offline-probe"',
                'model_providers.offline-probe.name="Offline probe"',
                f'model_providers.offline-probe.base_url="http://127.0.0.1:{server.server_port}"',
                'model_providers.offline-probe.wire_api="responses"',
                "model_providers.offline-probe.requires_openai_auth=false",
                "features.enable_request_compression=false",
                "features.multi_agent=false",
                "features.multi_agent_v2=false",
                "features.plugins=false",
                "features.apps=false",
                "features.remote_plugin=false",
                REASONING_OVERRIDES[0],
            ]
            command = [executable, "exec", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox"]
            for setting in settings:
                command.extend(["-c", setting])
            command.append("Offline transport test. Return OK without using tools.")
            # Use a process-local empty Codex configuration and no provider credentials.
            environment = {
                key: value for key, value in os.environ.items()
                if not key.startswith(("CODEX_", "OPENAI_", "GH_", "GITHUB_"))
            }
            environment["CODEX_HOME"] = temporary
            try:
                subprocess.run(command, cwd=temporary, env=environment, capture_output=True, timeout=45, check=False)
            finally:
                server.shutdown()
                thread.join(timeout=5)
    if not observations:
        raise ValueError(f"Codex produced no local request for {model}")
    if any(record["model"] != model for record in observations):
        raise ValueError("Codex changed the requested model")
    if any(record["reasoning_effort"] != "high" for record in observations):
        raise ValueError(f"Codex omitted high reasoning for {model}")
    return {"model": model, "reasoning_effort": observations[0]["reasoning_effort"], "requests_checked": len(observations)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex", default="codex", help="Path to the installed Codex executable")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    version = subprocess.check_output([args.codex, "--version"], text=True).strip()
    if version != f"codex-cli {CODEX_VERSION}":
        raise SystemExit(f"Expected codex-cli {CODEX_VERSION}, got {version}")
    evidence = {"schema_version": 1, "codex_version": CODEX_VERSION, "transport": "local_stub", "models": []}
    for model in MODELS:
        evidence["models"].append(capture_request(args.codex, model))
        print(f"Verified local Codex request: {model}, reasoning high")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write("### Verified Codex request settings\n\n")
            stream.write(f"Codex **{CODEX_VERSION}**, local HTTP request capture without inference:\n\n")
            for model in MODELS:
                stream.write(f"- **{model} — high reasoning**\n")


if __name__ == "__main__":
    main()
