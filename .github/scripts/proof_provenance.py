"""Known-bad provenance shared by the regression-proof and net-proof ledgers.

A known-bad state is a published package, a commit that existed on dev
(`dev-commit`), or a commit of the pull request that introduced and fixed the
defect before it reached dev (`pr-commit`). A `pr-commit` names its PR and,
when that PR lives in another repository (a fork), the repository as
`owner/name`:

    {"kind": "pr-commit", "commit": "<40 hex>", "pr": 133, "repository": "JKamsker/LiteDB"}

Without `repository` the PR is the upstream's own: its head is fetched from the
`origin` remote into `refs/proof/pr-<pr>`, exactly as before the field existed.
With it, the head is fetched from https://github.com/<owner>/<name>.git (public
repositories need no credentials) into `refs/proof/repo/<owner>/<name>/pr-<pr>`,
so a fork's PR #N never resolves against the upstream's PR #N.
"""
import re
import subprocess

import safety_common as common

KINDS = ("package", "dev-commit", "pr-commit")
COMMIT_KINDS = ("dev-commit", "pr-commit")
SHA = re.compile(r"[0-9a-f]{40}\Z")
# GitHub owner (letters, digits, single hyphens) / repository name (letters, digits, '.', '_', '-').
REPOSITORY = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\Z")
# Where `repository` is fetched from; the unit tests point it at local repositories.
GIT_HOST = "https://github.com/"


def is_pr_number(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def valid_repository(value):
    """True for an `owner/name` that is also safe inside a ref name."""
    return (isinstance(value, str) and REPOSITORY.match(value) is not None and ".." not in value
            and not value.endswith((".lock", ".git", ".")))


def check_shape(bad, label, report, path, kinds=KINDS):
    """Offline checks of a knownBad object; returns its kind when the shape is usable, else None."""
    if not isinstance(bad, dict):
        report.error(f"{label}: knownBad must be an object", path)
        return None
    kind = bad.get("kind")
    if kind not in kinds:
        report.error(f"{label}: knownBad.kind must be one of {', '.join(kinds)} (a mutant is not a known-bad state)",
                     path)
        return None
    if kind == "package":
        if not bad.get("version"):
            report.error(f"{label}: a package known-bad state names its published version", path)
    elif not SHA.match(str(bad.get("commit", ""))):
        report.error(f"{label}: knownBad.commit must be a full 40-character commit id", path)
    if kind == "pr-commit" and not is_pr_number(bad.get("pr")):
        report.error(f"{label}: a pr-commit known-bad state names its originating PR number", path)
    if "repository" in bad:
        if kind != "pr-commit":
            report.error(f"{label}: knownBad.repository only qualifies a pr-commit (a {kind} is upstream's own)", path)
        elif not valid_repository(bad.get("repository")):
            report.error(f"{label}: knownBad.repository must be a GitHub repository as 'owner/name', "
                         f"not {bad.get('repository')!r}", path)
    return kind


def proof_ref(bad):
    """The local ref that holds the head of a pr-commit's originating PR."""
    repository = bad.get("repository")
    if repository:
        return f"refs/proof/repo/{repository}/pr-{bad.get('pr')}"
    return f"refs/proof/pr-{bad.get('pr')}"


def source(bad):
    """What `git fetch` reads the PR head from: the origin remote or the named repository's URL."""
    repository = bad.get("repository")
    return f"{GIT_HOST}{repository}.git" if repository else "origin"


def describe(bad):
    repository = bad.get("repository")
    return f"PR #{bad.get('pr')} of {repository}" if repository else \
        f"PR #{bad.get('pr')} of the upstream repository (origin)"


def fetch_pr_head(bad, cwd=None):
    """Fetch the PR head into proof_ref(bad); returns git's exit code (failures are reported by the caller)."""
    refspec = f"+refs/pull/{bad.get('pr')}/head:{proof_ref(bad)}"
    return subprocess.run(["git", "fetch", "-q", "--no-tags", source(bad), refspec], cwd=cwd or common.repo_root(),
                          stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=False).returncode


def run(command, cwd=None):
    return subprocess.run(command, cwd=cwd or common.repo_root(), stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL).returncode


def has_commit(commit, cwd=None):
    return run(["git", "cat-file", "-e", f"{commit}^{{commit}}"], cwd) == 0


def ensure_commit(bad, cwd=None):
    """Make a commit known-bad state available locally (fetching its PR head when needed)."""
    commit = str(bad.get("commit"))
    if not has_commit(commit, cwd) and bad.get("kind") == "pr-commit":
        fetch_pr_head(bad, cwd)
    return has_commit(commit, cwd)


def check_commit(bad, label, report, dev_ref, offline=False, cwd=None):
    """Git checks that a commit known-bad state is real and correctly classified.

    Offline, nothing is fetched: the commit and the PR ref must already be local. A
    failed fetch is a warning, as it always was silent: the checks below then judge
    whatever the clone already holds and fail when that is not enough."""
    commit = str(bad.get("commit"))
    kind = bad.get("kind")
    if kind == "pr-commit" and not offline:
        if fetch_pr_head(bad, cwd) != 0:
            report.warning(f"{label}: could not fetch the head of {describe(bad)} from {source(bad)}")
    hint = "" if bad.get("repository") else \
        "; a commit of a fork's pull request names the fork as knownBad.repository ('owner/name')"
    if not has_commit(commit, cwd):
        report.error(f"{label}: known-bad commit {commit} is not available in this clone"
                     + (f" after fetching {describe(bad)}{hint}" if kind == "pr-commit" else ""))
        return
    on_dev = run(["git", "merge-base", "--is-ancestor", commit, dev_ref], cwd) == 0
    if kind == "dev-commit" and not on_dev:
        report.error(f"{label}: {commit} never existed on {dev_ref}; use pr-commit with its originating PR")
    if kind == "pr-commit":
        if on_dev:
            report.error(f"{label}: {commit} is on {dev_ref}; classify it as dev-commit")
        elif run(["git", "merge-base", "--is-ancestor", commit, proof_ref(bad)], cwd) != 0:
            report.error(f"{label}: {commit} is not part of PR #{bad.get('pr')} "
                         f"({describe(bad)}, {proof_ref(bad)}){hint}")
