---
name: Wholesale bugfix validation worker
run-name: Bugfix review ${{ inputs.request_id || 'registration' }}
description: Independently review one immutable candidate from one assigned perspective.
on:
  bots: ["github-actions[bot]"]
  push:
    branches: [automation/wholesale-bugfix]
    paths:
      - .github/workflows/bugfix-validate.md
      - .github/workflows/bugfix-validate.lock.yml
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
        description: Integration base before the fix
        required: true
        type: string
      candidate_sha:
        description: Exact candidate commit to review
        required: true
        type: string
      test_source_sha:
        description: Frozen regression revision
        required: true
        default: dd937719f7eee53c512f50ac604cab639bf42a4c
        type: string
      role:
        description: Independent reviewer perspective
        required: true
        type: choice
        options: [behavior, compatibility, lifecycle]
      source_run:
        description: Candidate check run
        required: false
        default: ""
        type: string
if: github.event_name == 'workflow_dispatch'
permissions:
  contents: read
checkout:
  ref: ${{ inputs.candidate_sha }}
  fetch-depth: 0
concurrency:
  group: wholesale-bugfix-review-${{ inputs.issue }}-${{ inputs.role }}
  cancel-in-progress: false
timeout-minutes: 30
max-ai-credits: 1000
max-turns: 80
sandbox:
  agent:
    version: v0.27.43
network:
  allowed: [defaults, dotnet]
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
  model: gpt-5.6-sol
steps:
  - name: Prepare immutable review contract
    env:
      BUGFIX_ISSUE: ${{ inputs.issue }}
      BUGFIX_BASE_SHA: ${{ inputs.base_sha }}
      BUGFIX_CANDIDATE_SHA: ${{ inputs.candidate_sha }}
      BUGFIX_TEST_SOURCE_SHA: ${{ inputs.test_source_sha }}
      BUGFIX_ROLE: ${{ inputs.role }}
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
          (".github/scripts/probe_gh_aw_reasoning.py", "probe.py"),
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
  - name: Collect immutable review evidence
    env:
      BUGFIX_ISSUE: ${{ inputs.issue }}
      BUGFIX_BASE_SHA: ${{ inputs.base_sha }}
      BUGFIX_CANDIDATE_SHA: ${{ inputs.candidate_sha }}
      BUGFIX_TEST_SOURCE_SHA: ${{ inputs.test_source_sha }}
      BUGFIX_ROLE: ${{ inputs.role }}
      BUGFIX_SOURCE_RUN: ${{ inputs.source_run }}
      BUGFIX_MODEL: gpt-5.6-sol
      BUGFIX_REASONING_EFFORT: high
      GITHUB_WORKFLOW_SHA: ${{ github.workflow_sha }}
    run: python3 "$RUNNER_TEMP/gh-aw/bugfix-control/collect.py" --manifest "$RUNNER_TEMP/gh-aw/bugfix-control/issues.json"
  - name: Redact Codex endpoint artifacts
    if: always()
    env:
      CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}
    run: python3 "$RUNNER_TEMP/gh-aw/bugfix-control/redact.py"
  - name: Upload review evidence
    uses: actions/upload-artifact@v4
    with:
      name: bugfix-review-${{ inputs.issue }}-${{ inputs.role }}
      path: |
        /tmp/gh-aw/bugfix/result.json
        /tmp/gh-aw/bugfix/metadata.json
        /tmp/gh-aw/bugfix/runtime-proof.json
      if-no-files-found: error
      retention-days: 90
---

# Independent regression review

Perform this task yourself. Do not delegate, spawn child agents, or launch another
agent or model process. The controller dispatches all three independent reviewers.

Read `/tmp/gh-aw/bugfix/task.json` first. Review the full diff from its `base_sha`
to `candidate_sha`, the frozen regression contract, relevant callers, and adjacent
tests. Form your own conclusions without reading other validators' reports.

Read `contract.review_requirements` when present. Address every requirement for
your role explicitly in `coverage`, with concrete code evidence or executed
checks and their outcomes. These requirements supplement the role guidance below.
If a required conclusion lacks evidence, use `inconclusive` and describe the
missing evidence in `findings`; use `changes_requested` for a supported defect.
Do not approve a candidate while a requirement for your role remains unverified.

Perform the perspective selected by `role`:

- **behavior**: Verify the reported API contract, boundary values, alternate
  inputs, neighboring APIs, and whether the regression can pass for the wrong
  reason. Trace valid-input behavior as well as rejection paths.
- **compatibility**: Assess persisted bytes, old database files, v7 upgrade,
  ordinary v8 preservation, v9 vector promotion, encryption, read-only access,
  WAL replay, checkpoint, rollback, and reopen integrity where reachable. Explain
  with code evidence which boundaries the changed code can and cannot affect.
- **lifecycle**: Assess transaction behavior, concurrency, disposal, cancellation,
  error paths, resource ownership, and performance effects where reachable.

Run relevant checks when feasible. Keep the repository completely unchanged;
use `/tmp` for scratch work. Do not commit, edit files, create pull requests, send
messages, or print credentials or endpoint settings. Do not label unexecuted
checks as passing. Scope conclusions to evidence you actually inspected.

Write `/tmp/gh-aw/bugfix/result.json` with exactly the `identity` fields from the
task (`schema_version`, `issue`, `base_sha`, `candidate_sha`, `test_source_sha`,
`role`) plus:

- `verdict`: `pass`, `changes_requested`, or `inconclusive`
- `findings`: an array of objects, each containing nonempty `summary`, `path`,
  and `evidence` strings. Pass requires an empty array; other verdicts require at
  least one actionable finding or concrete reason validation is inconclusive.
- `coverage`: a nonempty array of concrete descriptions of inspected code paths,
  executed checks and outcomes, and material limitations

Use `changes_requested` for supported regressions and `inconclusive` when a
required conclusion lacks evidence. Approval is specific to the exact candidate.
