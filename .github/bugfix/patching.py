"""Apply a validated worker patch in an isolated checkout and publish one candidate."""

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import sys
import tempfile

from artifacts import read_members, validate_worker_model
from state import require
from storage import run


def git(repository, *arguments):
    return run(["git", "-C", str(repository), "-c", "credential.helper=", "-c",
                "credential.helper=!gh auth git-credential", *map(str, arguments)])


@contextmanager
def worktree(repository, sha):
    with tempfile.TemporaryDirectory(prefix="litedb-bugfix-") as directory:
        path = Path(directory) / "checkout"
        require(path.resolve().parent == Path(directory).resolve() and not path.is_symlink(), "Unsafe temporary worktree path")
        git(repository, "worktree", "add", "--quiet", "--detach", path, sha)
        try:
            yield path
        finally:
            require(path.resolve().parent == Path(directory).resolve() and not path.is_symlink(), "Worktree cleanup path changed")
            git(repository, "worktree", "remove", "--force", path)


def worker_payload(data, state, source_sha, run_id):
    files = read_members(data, ("patch.diff", "result.json", "metadata.json", "runtime-proof.json"))
    require(set(files) == {"patch.diff", "result.json", "metadata.json", "runtime-proof.json"}, "Incomplete fix artifact")
    require(0 < len(files["patch.diff"]) <= 2 * 1024 * 1024, "Empty or oversized worker patch")
    result = json.loads(files["result.json"])
    metadata = json.loads(files["metadata.json"])
    require(isinstance(result, dict) and isinstance(metadata, dict), "Worker reports must be JSON objects")
    validate_worker_model(metadata, "fix", files["runtime-proof.json"])
    expected = {"schema_version": 1, "issue": state["issue"], "base_sha": source_sha,
                "test_source_sha": state["test_source_sha"]}
    for report in (result, metadata):
        for field, value in expected.items():
            require(type(report.get(field)) is type(value) and report[field] == value, f"Fix identity mismatch: {field}")
    require(result.get("status") == "proposed", "Worker did not propose a fix")
    require(isinstance(result.get("summary"), str) and result["summary"].strip(), "Missing fix explanation")
    tests = result.get("tests")
    require(isinstance(tests, list) and tests and all(isinstance(item, str) and item.strip() for item in tests),
            "Worker omitted actual validation descriptions")
    require(metadata.get("kind") == "fix" and metadata.get("workflow_sha") == state["workflow_sha"],
            "Fix metadata workflow mismatch")
    require(metadata.get("run_id") == str(run_id), "Fix metadata run mismatch")
    for filename, field in (("patch.diff", "patch_sha256"), ("result.json", "result_sha256")):
        require(metadata.get(field) == hashlib.sha256(files[filename]).hexdigest(), f"Worker {field} mismatch")
    paths = metadata.get("changed_paths")
    require(isinstance(paths, list) and paths and all(isinstance(path, str) for path in paths)
            and len(paths) == len(set(paths)), "Invalid worker changed paths")
    return files["patch.diff"], result, metadata


def create_candidate(repository, control, repo, state, source_sha, run_id, data):
    patch, result, metadata = worker_payload(data, state, source_sha, run_id)
    manifest = control / "scripts/bugfix/issues.json"
    contract = json.loads(manifest.read_text(encoding="utf-8"))["issues"][str(state["issue"])]
    require(set(metadata["changed_paths"]) <= set(contract["allowed_production_paths"]), "Worker patch exceeds approved scope")
    git(repository, "fetch", "--quiet", f"https://github.com/{repo}.git", source_sha)
    with worktree(repository, source_sha) as candidate:
        with tempfile.TemporaryDirectory(prefix="litedb-bugfix-patch-") as directory:
            patch_path = Path(directory) / "patch.diff"
            patch_path.write_bytes(patch)
            git(candidate, "apply", "--check", "--index", patch_path)
            git(candidate, "apply", "--index", patch_path)
            changes = git(candidate, "diff", "--cached", "--name-status", "--no-renames").splitlines()
            actual = []
            for entry in changes:
                status, path = entry.split("\t", 1)
                require(status == "M" and path in contract["allowed_production_paths"], "Patch changes unapproved file type or path")
                actual.append(path)
            require(sorted(actual) == sorted(metadata["changed_paths"]), "Patch differs from worker path evidence")
            hooks = Path(directory) / "hooks"
            hooks.mkdir()
            message = (f"Fix issue #{state['issue']} regression\n\n{result['summary']}\n\n"
                       "Preserve the frozen regression contract while correcting the reported behavior.")
            git(candidate, "-c", f"core.hooksPath={hooks}", "-c", "user.name=LiteDB bugfix controller",
                "-c", "user.email=bugfix-controller@users.noreply.github.com", "-c", "commit.gpgsign=false",
                "commit", "--quiet", "-m", message)
            sha = git(candidate, "rev-parse", "HEAD")
            run([sys.executable, str(control / "scripts/bugfix/gate.py"), "protect", "--manifest", str(manifest),
                 "--issue", str(state["issue"]), "--base-sha", state["base_sha"], "--candidate-sha", sha,
                 "--repository", str(candidate), "--output", str(Path(directory) / "scope.json")])
            run([sys.executable, str(control / "scripts/check-csharp-size.py"), "--base", state["base_sha"]], cwd=candidate)
            return sha, metadata


def publish_candidate(repository, repo, branch, sha):
    require(branch.startswith("fix/issue-"), "Candidate branch must be in the fix namespace")
    target = f"https://github.com/{repo}.git"
    ref = f"refs/heads/{branch}"
    current = git(repository, "ls-remote", "--heads", target, ref)
    if current:
        require(current.split()[0] == sha, "Candidate branch already points to a different commit")
        return
    git(repository, "push", "--quiet", f"--force-with-lease={ref}:", target, f"{sha}:{ref}")
