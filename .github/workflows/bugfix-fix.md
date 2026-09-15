---
name: Wholesale bugfix fix worker
run-name: Bugfix fix ${{ inputs.request_id || 'registration' }}
description: Propose one restricted production patch for a confirmed regression.
on:
  push:
    branches: [automation/wholesale-bugfix]
    paths:
      - .github/workflows/bugfix-fix.md
      - .github/workflows/bugfix-fix.lock.yml
  workflow_dispatch:
    inputs:
      request_id:
        description: Controller correlation ID
        required: false
        default: ""
        type: string
      issue:
        description: Eligible issue number
        required: true
        type: string
      base_sha:
        description: Immutable source commit for this attempt
        required: true
        type: string
      test_source_sha:
        description: Frozen regression revision
        required: true
        default: dd937719f7eee53c512f50ac604cab639bf42a4c
        type: string
      source_run:
        description: Prior failing check run, if applicable
        required: false
        default: ""
        type: string
if: github.event_name == 'workflow_dispatch'
permissions:
  contents: read
checkout:
  ref: ${{ inputs.base_sha }}
  fetch-depth: 0
concurrency:
  group: wholesale-bugfix-fix-${{ inputs.issue }}
  cancel-in-progress: false
timeout-minutes: 30
max-ai-credits: 2000
max-turns: 100
sandbox:
  agent:
    version: v0.27.43
network:
  allowed: [defaults, dotnet]
tools:
  bash: true
engine:
  id: codex
  model: gpt-6-astra
steps:
  - name: Prepare immutable worker contract
    env:
      BUGFIX_ISSUE: ${{ inputs.issue }}
      BUGFIX_BASE_SHA: ${{ inputs.base_sha }}
      BUGFIX_TEST_SOURCE_SHA: ${{ inputs.test_source_sha }}
      BUGFIX_SOURCE_RUN: ${{ inputs.source_run }}
      GITHUB_WORKFLOW_SHA: ${{ github.workflow_sha }}
    run: |
      python3 - <<'PY'
      import importlib.util
      import json
      import os
      import re
      import subprocess
      from pathlib import Path

      trusted_sha = os.environ["GITHUB_WORKFLOW_SHA"]
      if not re.fullmatch(r"[0-9a-f]{40}", trusted_sha):
          raise SystemExit("Invalid trusted workflow revision")
      control = Path(os.environ["RUNNER_TEMP"]) / "gh-aw" / "bugfix-control"
      control.mkdir(parents=True, exist_ok=True)
      for source, target in (
          (".github/scripts/collect_bugfix_worker.py", "collect.py"),
          (".github/scripts/redact_gh_aw_codex_artifacts.py", "redact.py"),
          ("scripts/bugfix/issues.json", "issues.json"),
      ):
          data = subprocess.check_output(["git", "show", f"{trusted_sha}:{source}"])
          (control / target).write_bytes(data)
      spec = importlib.util.spec_from_file_location("collector", control / "collect.py")
      collector = importlib.util.module_from_spec(spec)
      spec.loader.exec_module(collector)
      expected = collector.identity(dict(os.environ))
      contract = collector.validate_contract(Path.cwd(), json.loads((control / "issues.json").read_text()), expected)
      output = Path("/tmp/gh-aw/bugfix")
      output.mkdir(parents=True, exist_ok=True)
      (output / "task.json").write_text(json.dumps({"identity": expected, "contract": contract, "source_run": os.environ.get("BUGFIX_SOURCE_RUN", "")}, indent=2))
      PY
  - name: Setup .NET 8
    uses: actions/setup-dotnet@v4
    with:
      dotnet-version: 8.0.x
post-steps:
  - name: Collect restricted fix evidence
    env:
      BUGFIX_ISSUE: ${{ inputs.issue }}
      BUGFIX_BASE_SHA: ${{ inputs.base_sha }}
      BUGFIX_TEST_SOURCE_SHA: ${{ inputs.test_source_sha }}
      BUGFIX_SOURCE_RUN: ${{ inputs.source_run }}
      BUGFIX_MODEL: gpt-6-astra
      GITHUB_WORKFLOW_SHA: ${{ github.workflow_sha }}
    run: python3 "$RUNNER_TEMP/gh-aw/bugfix-control/collect.py" --manifest "$RUNNER_TEMP/gh-aw/bugfix-control/issues.json"
  - name: Redact Codex endpoint artifacts
    if: always()
    env:
      CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}
    run: python3 "$RUNNER_TEMP/gh-aw/bugfix-control/redact.py"
  - name: Upload fix evidence
    uses: actions/upload-artifact@v4
    with:
      name: bugfix-fix-${{ inputs.issue }}
      path: |
        /tmp/gh-aw/bugfix/patch.diff
        /tmp/gh-aw/bugfix/result.json
        /tmp/gh-aw/bugfix/metadata.json
      if-no-files-found: error
      retention-days: 90
---

# Fix one confirmed regression

Read `/tmp/gh-aw/bugfix/task.json` first. Its `identity` fields must appear
unchanged in your result. Read the listed frozen regression tests and production
code. Implement the smallest maintainable fix that satisfies that contract and
preserves valid inputs and existing behavior. Follow repository C# conventions.

Only modify existing files explicitly listed under `allowed_production_paths`.
Keep HEAD unchanged; do not commit, stage, create files inside the repository,
edit tests or automation, create pull requests, or send messages. The deterministic
controller will apply and test your exported patch. Use `/tmp` for scratch files.

Run the focused regression and relevant nearby tests where available, using
`TestingEnabled=true` and the manifest's exact filter. The branch intentionally
has unrelated failures: report what you executed and its actual result. Never
weaken tests or treat a build failure, missing test, or timeout as passing evidence.
Keep production and test-hook build outputs separate. If you cannot verify a
check in this environment, state that explicitly in the result's `tests` list.

Write `/tmp/gh-aw/bugfix/result.json` as JSON with exactly the identity fields
(`schema_version`, `issue`, `base_sha`, `test_source_sha`) plus:

- `status`: `proposed`
- `summary`: a concrete explanation of the change and why it fixes the defect
- `tests`: a nonempty array describing actual checks and their outcomes

Leave your production changes in the working tree for collection. Do not print
credentials, environment dumps, or endpoint settings. If no compliant fix can be
proposed, report the blocker instead of fabricating successful evidence.
