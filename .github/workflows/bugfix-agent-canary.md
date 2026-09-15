---
name: Wholesale bugfix agent canary
description: Verify the Codex worker can read the regression contract and return checked evidence.
on:
  bots: ["github-actions[bot]"]
  workflow_dispatch:
permissions:
  contents: read
concurrency:
  group: wholesale-bugfix-agent-canary
  cancel-in-progress: false
timeout-minutes: 15
max-ai-credits: 500
max-turns: 20
sandbox:
  agent:
    version: v0.27.43
network: defaults
tools:
  bash: true
safe-outputs:
  report-failure-as-issue: false
  missing-tool: false
  missing-data: false
  report-incomplete: false
  noop:
    report-as-issue: false
  threat-detection: false
  # A non-builtin output prevents this compiler from auto-enabling create-issue.
  scripts:
    record-completion:
      description: Record completion in the run without changing GitHub resources.
      script: |
        return { success: true };
engine:
  id: codex
  version: "0.154.0"
  model: gpt-6-astra
steps:
  - name: Stage trusted runtime request probe
    run: |
      mkdir -p "$RUNNER_TEMP/gh-aw/bugfix-control"
      cp .github/scripts/probe_gh_aw_reasoning.py "$RUNNER_TEMP/gh-aw/bugfix-control/probe.py"
post-steps:
  - name: Validate canary evidence
    run: |
      python3 - <<'PY'
      import json
      import os
      import subprocess
      from pathlib import Path

      expected = {
          "status": "ok",
          "source_sha": "dd937719f7eee53c512f50ac604cab639bf42a4c",
          "issue": 2874,
          "test_file": "LiteDB.Tests/Issues/Issue2874_Tests.cs",
      }
      path = Path("/tmp/gh-aw/bugfix-canary.json")
      if not path.is_file() or json.loads(path.read_text()) != expected:
          raise SystemExit("Canary did not produce the required evidence")
      status = subprocess.check_output(["git", "status", "--porcelain"], text=True)
      if status.strip():
          raise SystemExit("Canary changed the repository workspace")
      proof = Path(os.environ["RUNNER_TEMP"]) / "gh-aw" / "bugfix-control" / "runtime-proof.json"
      Path("/tmp/gh-aw/codex-runtime-proof.json").write_bytes(proof.read_bytes())
      print("Codex canary returned the expected regression contract")
      PY
  - name: Redact Codex endpoint artifacts
    if: always()
    env:
      CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}
    run: python3 .github/scripts/redact_gh_aw_codex_artifacts.py
  - name: Upload canary evidence
    uses: actions/upload-artifact@v4
    with:
      name: bugfix-agent-canary
      path: |
        /tmp/gh-aw/bugfix-canary.json
        /tmp/gh-aw/codex-runtime-proof.json
      if-no-files-found: error
      retention-days: 30
---

# Read-only Codex canary

Perform this task yourself. Do not delegate, spawn child agents, or launch another
agent or model process. The controller owns all independent agent scheduling.

Read `LiteDB.Tests/Issues/Issue2874_Tests.cs` in this checkout. Confirm it
contains the argument-validation regression tests for issue 2874. This checkout
is based on source commit `dd937719f7eee53c512f50ac604cab639bf42a4c`.

After reading the test file, create `/tmp/gh-aw/bugfix-canary.json` with exactly:

```json
{"status":"ok","source_sha":"dd937719f7eee53c512f50ac604cab639bf42a4c","issue":2874,"test_file":"LiteDB.Tests/Issues/Issue2874_Tests.cs"}
```

Leave the repository workspace unchanged. This canary needs no build, tests,
network lookups, pull requests, or messages. Do not read or print credentials,
environment dumps, or endpoint settings. If the specified test file does not
contain this regression contract, report the discrepancy and do not create the
evidence file. End with a short completion message.
