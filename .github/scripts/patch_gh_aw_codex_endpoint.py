#!/usr/bin/env python3
"""Patch generated gh-aw lockfiles to read the OpenAI proxy target from a secret.

gh-aw can generate an AWF OpenAI proxy target from a literal OPENAI_BASE_URL, but
the host for this repository is intentionally secret. This patch keeps the
generated Codex configuration intact and injects a small runtime step that reads
CODEX_LB_BASE_URL on the runner, configures AWF's OpenAI target, and then keeps
that secret out of the sandboxed agent environment.
"""

from __future__ import annotations

import sys
from pathlib import Path


CONFIG_WRITE = '> "${RUNNER_TEMP}/gh-aw/awf-config.json"'
AWF_SCHEMA = "awf-config.schema.json"
AWF_COMMAND = "sudo -E awf --config"
PATCH_MARKER = "Patch gh-aw OpenAI proxy target from CODEX_LB_BASE_URL"
CODEX_CONFIG_HEREDOC = 'cat > "/tmp/gh-aw/mcp-config/config.toml" << GH_AW_CODEX_SHELL_POLICY_'
REASONING_EFFORT_LINE = 'model_reasoning_effort = "high"'
CODEX_ENDPOINT_ENV = "          CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}"
DETECTION_UPLOAD_STEP = "      - name: Upload threat detection log"
DETECTION_REDACTION_MARKER = "Redact Codex endpoint detection artifacts"


PATCH_SNIPPET = [
    "# Patch gh-aw OpenAI proxy target from CODEX_LB_BASE_URL.",
    "python3 - <<'PY'",
    "import json",
    "import os",
    "from pathlib import Path",
    "from urllib.parse import urlparse",
    "",
    'endpoint = os.environ["CODEX_LB_BASE_URL"].strip().rstrip("/")',
    "parsed = urlparse(endpoint)",
    'if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:',
    '    raise SystemExit("CODEX_LB_BASE_URL must be an absolute HTTPS URL with only a hostname, optional port, and optional path")',
    "host = parsed.hostname",
    "target_host = parsed.netloc",
    'base_path = parsed.path.rstrip("/")',
    'print(f"::add-mask::{endpoint}")',
    'print(f"::add-mask::{host}")',
    'print(f"::add-mask::{target_host}")',
    'config_path = Path(os.environ["RUNNER_TEMP"]) / "gh-aw" / "awf-config.json"',
    "config = json.loads(config_path.read_text())",
    'allow_domains = config.setdefault("network", {}).setdefault("allowDomains", [])',
    "if host not in allow_domains:",
    "    allow_domains.append(host)",
    'openai_target = config.setdefault("apiProxy", {}).setdefault("targets", {}).setdefault("openai", {})',
    'openai_target["host"] = target_host',
    "if base_path:",
    '    openai_target["basePath"] = base_path',
    "else:",
    '    openai_target.pop("basePath", None)',
    'config_path.write_text(json.dumps(config, separators=(",", ":"), ensure_ascii=False) + "\\n")',
    "PY",
]

DETECTION_REDACTION_STEP = [
    f"      - name: {DETECTION_REDACTION_MARKER}",
    "        if: always() && steps.detection_guard.outputs.run_detection == 'true'",
    "        env:",
    "          CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}",
    "        run: |",
    "          python3 - <<'PY'",
    "          import os",
    "          from pathlib import Path",
    "          from urllib.parse import urlparse",
    "",
    '          replacement = b"[REDACTED_CODEX_LB]"',
    '          endpoint = os.environ.get("CODEX_LB_BASE_URL", "").strip().rstrip("/")',
    "          if not endpoint:",
    "              raise SystemExit(0)",
    "          parsed = urlparse(endpoint)",
    "          needles = {value for value in (endpoint, parsed.netloc, parsed.hostname) if value}",
    "          if parsed.hostname and parsed.port:",
    '              needles.add(f"{parsed.hostname}:{parsed.port}")',
    "          ordered_needles = [needle.encode() for needle in sorted(needles, key=len, reverse=True)]",
    "          redacted_count = 0",
    '          for path in Path("/tmp/gh-aw/threat-detection").rglob("*"):',
    "              if not path.is_file():",
    "                  continue",
    "              try:",
    "                  data = path.read_bytes()",
    "              except OSError:",
    "                  continue",
    '              if b"\\0" in data[:4096]:',
    "                  continue",
    "              redacted = data",
    "              for needle in ordered_needles:",
    "                  redacted = redacted.replace(needle, replacement)",
    "              if redacted != data:",
    "                  path.write_bytes(redacted)",
    "                  redacted_count += 1",
    '          print(f"Redacted Codex endpoint detection artifacts: {redacted_count} file(s)")',
    "          PY",
]


def insert_runtime_patch(lines: list[str]) -> tuple[list[str], int]:
    patched: list[str] = []
    insertions = 0
    for index, line in enumerate(lines):
        patched.append(line)
        if CONFIG_WRITE not in line or AWF_SCHEMA not in line:
            continue
        lookahead = "\n".join(lines[index + 1 : index + 45])
        if PATCH_MARKER in lookahead:
            continue

        indent = line[: len(line) - len(line.lstrip())]
        patched.extend(
            "" if snippet_line == "" else f"{indent}{snippet_line}"
            for snippet_line in PATCH_SNIPPET
        )
        insertions += 1
    return patched, insertions


def patch_awf_command(line: str) -> tuple[str, bool]:
    if AWF_COMMAND not in line:
        return line, False
    if "--exclude-env CODEX_LB_BASE_URL" in line:
        return line, False
    if "--env-all " not in line:
        raise RuntimeError("Found awf command without --env-all")
    return line.replace(
        "--env-all ",
        "--env-all --exclude-env CODEX_LB_BASE_URL ",
        1,
    ), True


def insert_endpoint_env(lines: list[str]) -> tuple[list[str], int]:
    patched = list(lines)
    insertions = 0
    awf_indices = [index for index, line in enumerate(patched) if AWF_COMMAND in line]

    for awf_index in reversed(awf_indices):
        next_step = len(patched)
        for index in range(awf_index + 1, len(patched)):
            if patched[index].startswith("      - name: "):
                next_step = index
                break

        env_index = None
        for index in range(awf_index + 1, next_step):
            if patched[index] == "        env:":
                env_index = index
                break
        if env_index is None:
            raise RuntimeError("Found awf command without a following env block")

        if any("CODEX_LB_BASE_URL:" in line for line in patched[env_index + 1 : next_step]):
            continue
        patched.insert(env_index + 1, CODEX_ENDPOINT_ENV)
        insertions += 1

    return patched, insertions


def insert_codex_reasoning_effort(lines: list[str]) -> tuple[list[str], int]:
    patched: list[str] = []
    insertions = 0

    for index, line in enumerate(lines):
        patched.append(line)
        if CODEX_CONFIG_HEREDOC not in line:
            continue

        lookahead = "\n".join(lines[index + 1 : index + 12])
        if REASONING_EFFORT_LINE in lookahead:
            continue

        indent = line[: len(line) - len(line.lstrip())]
        patched.append(f"{indent}{REASONING_EFFORT_LINE}")
        insertions += 1

    return patched, insertions


def insert_detection_redaction(lines: list[str]) -> tuple[list[str], int]:
    marker_line = f"      - name: {DETECTION_REDACTION_MARKER}"
    if marker_line in lines:
        patched: list[str] = []
        replaced = False
        index = 0
        while index < len(lines):
            if lines[index] != marker_line:
                patched.append(lines[index])
                index += 1
                continue

            patched.extend(DETECTION_REDACTION_STEP)
            replaced = True
            index += 1
            while index < len(lines) and not lines[index].startswith("      - name: "):
                index += 1

        return patched, int(replaced)

    patched: list[str] = []
    insertions = 0
    for line in lines:
        if line == DETECTION_UPLOAD_STEP:
            patched.extend(DETECTION_REDACTION_STEP)
            insertions += 1
        patched.append(line)
    return patched, insertions


def patch_lockfile(path: Path) -> bool:
    text = path.read_text()
    if "codex_harness.cjs" not in text:
        return False

    lines = text.splitlines()
    lines, snippet_count = insert_runtime_patch(lines)

    awf_patch_count = 0
    for index, line in enumerate(lines):
        lines[index], changed = patch_awf_command(line)
        if changed:
            awf_patch_count += 1

    lines, env_count = insert_endpoint_env(lines)
    lines, reasoning_effort_count = insert_codex_reasoning_effort(lines)
    lines, detection_redaction_count = insert_detection_redaction(lines)
    lines = [line.rstrip() for line in lines]
    while lines and lines[-1] == "":
        lines.pop()

    if snippet_count == 0 and PATCH_MARKER not in "\n".join(lines):
        raise RuntimeError(f"{path} was not patched; no AWF config write was found")
    if "--exclude-env CODEX_LB_BASE_URL" not in "\n".join(lines):
        raise RuntimeError(f"{path} was not patched; no AWF command was updated")
    if "CODEX_LB_BASE_URL: ${{ secrets.CODEX_LB_BASE_URL }}" not in "\n".join(lines):
        raise RuntimeError(f"{path} was not patched; CODEX_LB_BASE_URL env is missing")
    if REASONING_EFFORT_LINE not in "\n".join(lines):
        raise RuntimeError(f"{path} was not patched; Codex reasoning effort is missing")

    patched_text = "\n".join(lines) + "\n"
    if patched_text == text:
        return False
    path.write_text(patched_text)
    print(
        f"patched {path}: snippets={snippet_count}, "
        f"awf_commands={awf_patch_count}, env_blocks={env_count}, "
        f"reasoning_effort_blocks={reasoning_effort_count}, "
        f"detection_redaction_steps={detection_redaction_count}"
    )
    return True


def main() -> int:
    paths = [Path(arg) for arg in sys.argv[1:]]
    if not paths:
        paths = sorted(Path(".github/workflows").glob("*.lock.yml"))

    patched_any = False
    for path in paths:
        if not path.exists():
            raise FileNotFoundError(path)
        patched_any = patch_lockfile(path) or patched_any

    if not patched_any:
        print("no gh-aw Codex lockfile changes needed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
