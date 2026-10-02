"""Regression proofs: a fix must fail on a real known-bad state and pass on the candidate.

Each entry in .github/safety/regression-proofs.json pins a ReproRunner repro to a
real state in which the bug existed, in order of preference:

1. a published NuGet package (`package`, the strongest evidence);
2. a commit that existed on dev (`dev-commit`);
3. a commit of the originating PR that never reached dev (`pr-commit`); a PR of
   another repository (a fork) also names it as `repository: owner/name`
   (proof_provenance.py resolves it there, never against upstream's PR number).

A synthetic mutant is not a known-bad state. The repro's package variant is the
known-bad state and its latest variant is the candidate source; ReproRunner runs
both. Pull requests prove the entries they add or change (bad MUST FAIL,
candidate MUST PASS), the push to dev proves them once more on the integrated
revision, and afterwards the historical comparison retires: it only runs again
when its entry or repro changes. The permanent guard named by each entry keeps
the regression covered in the ordinary suites.

Subcommands: validate, select, new, pack-known-bad, verify. A bug-fix PR (a label in
BUG_LABELS) must add at least one proof (select --labels or --require-new-proof).
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

import proof_provenance as provenance
import repro_scaffold
import safety_common as common

LEDGER = f"{common.SAFETY_DIR}/regression-proofs.json"
REPROS = "LiteDB.ReproRunner/Repros"
# Changing any of this re-proves every entry. The scripts are the import closure of
# this module: an imported module's top-level code runs on every proof.
HARNESS = ("LiteDB.ReproRunner/LiteDB.ReproRunner.Cli/", "LiteDB.ReproRunner/LiteDB.ReproRunner.Shared/",
           "LiteDB.ReproRunner/Repros/SharedSelfTeardownProof/",
           ".github/scripts/regression_proof.py", ".github/scripts/repro_scaffold.py",
           ".github/scripts/proof_provenance.py", ".github/scripts/safety_common.py",
           ".github/workflows/regression-proof.yml")
NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/litedb/index.json"
KINDS = provenance.KINDS
OUTCOMES = ["Reproduce", "NoRepro", "HardFail", "Intermittent"]
STATES = ["Red", "Green", "Flaky"]
SHA = provenance.SHA
MIN_BUG_PROOFS = 1  # per bug-fix PR; add one proof per bug a PR fixes
# Labels that make a PR a bug fix. bugfix-fix marks the automated bugfix worker's fixes.
BUG_LABELS = ("bug", "bugfix-fix")


def is_bug_fix(labels):
    """True when a PR's labels make it a bug fix, which must add a regression proof."""
    return any(label in BUG_LABELS for label in labels)


def parse_labels(text):
    """Label names from the event's JSON array; null or empty outside pull requests."""
    names = json.loads(text) if text and text.strip() else None
    if names is None:
        return []
    if not isinstance(names, list) or not all(isinstance(name, str) for name in names):
        raise ValueError("--labels must be a JSON array of label names")
    return names


def known_bad_version(bad):
    """The LiteDB package version the repro's package variant must pin."""
    if bad.get("kind") == "package":
        return bad.get("version")
    return f"0.0.0-knownbad.{str(bad.get('commit', ''))[:12]}"


def load(tree, report):
    try:
        data = tree.read_json(LEDGER, {}) or {}
    except common.MalformedJson as error:
        report.error(str(error), LEDGER)
        return []
    return common.section(data, "proofs", list, report, LEDGER)


def validate_entry(tree, entry, report):
    """Offline checks: the repro, its pin, its fixed-state expectations and its permanent guard."""
    repro = entry.get("repro") or "<missing repro>"
    label = f"Regression proof {repro}"
    manifest = _json(tree, f"{REPROS}/{repro}/repro.json")
    if manifest is None:
        report.error(f"{label}: {REPROS}/{repro}/repro.json does not exist", LEDGER)
        return
    bad = entry.get("knownBad") if isinstance(entry.get("knownBad"), dict) else {}
    kind = provenance.check_shape(bad, label, report, LEDGER)
    if kind is None:
        return
    if kind != "package" and len((bad.get("reason") or "").strip()) < 20:
        report.error(f"{label}: explain why no published package containing the bug can be used", LEDGER)
    pinned = _pinned_version(tree, repro)
    if pinned != known_bad_version(bad):
        report.error(f"{label}: the repro's LiteDBPackageVersion is {pinned!r}; the known-bad state requires "
                     f"{known_bad_version(bad)!r}", f"{REPROS}/{repro}")
    _check_fixed_state(manifest, label, report)
    _check_guard(tree, entry, label, report)


def _json(tree, path):
    text = tree.read(path)
    try:
        return None if text is None else json.loads(text)
    except ValueError:
        return None


def _pinned_version(tree, repro):
    for path in tree.paths():
        if path.startswith(f"{REPROS}/{repro}/") and path.endswith(".csproj"):
            match = re.search(r"<LiteDBPackageVersion[^>]*>\s*([^<\s]+)\s*<", tree.read(path) or "")
            return match.group(1) if match else None
    return None


def _check_fixed_state(manifest, label, report):
    """Only a green repro whose package reproduces and whose latest does not can prove a fix."""
    if str(manifest.get("state", "")).lower() != "green":
        report.error(f"{label}: the repro state must be 'green' (the candidate must not reproduce)", LEDGER)
    outcomes = manifest.get("expectedOutcomes") or {}
    package = str((outcomes.get("package") or {}).get("kind", "reproduce")).lower()
    latest = str((outcomes.get("latest") or {}).get("kind", "noRepro")).lower()
    if package not in ("reproduce", "hardfail"):
        report.error(f"{label}: the known-bad variant must reproduce deterministically, not '{package}'", LEDGER)
    if latest != "norepro":
        report.error(f"{label}: the candidate variant must expect 'noRepro', not '{latest}'", LEDGER)


def _check_guard(tree, entry, label, report):
    guard = entry.get("permanentGuard")
    if not isinstance(guard, list) or not guard:
        report.error(f"{label}: name the permanent regression guard (tests, fuzz targets or scripts)", LEDGER)
        return
    targets, scheduled, workflows = None, None, None
    for reference in guard:
        reference = str(reference)
        if reference.startswith("fuzz:"):
            if targets is None:
                targets, scheduled = common.fuzz_targets(tree), common.scheduled_fuzz_targets(tree)
            if reference[5:] not in targets or reference[5:] not in scheduled:
                report.error(f"{label}: guard {reference} is not a fuzz target run by {common.FUZZ_WORKFLOW}", LEDGER)
        elif "#" in reference:
            if common.resolve_test(tree, reference) is None:
                report.error(f"{label}: guard test {reference} does not resolve", LEDGER)
        else:
            workflows = common.workflow_text(tree) if workflows is None else workflows
            if tree.read(reference) is None or reference not in workflows:
                report.error(f"{label}: guard {reference} does not exist or no workflow runs it", LEDGER)


def check_provenance(entry, report, dev_ref, offline=False):
    """Networked/git checks that the known-bad state is real and correctly classified."""
    bad, repro = entry.get("knownBad") or {}, entry.get("repro")
    label = f"Regression proof {repro}"
    if bad.get("kind") == "package":
        if offline:
            return
        try:
            versions = {normalize_version(version) for version in published_versions()}
        except (OSError, ValueError) as error:  # URLError is an OSError
            report.error(f"{label}: could not read the NuGet index to verify the package: {error}")
            return
        if normalize_version(bad.get("version", "")) not in versions:
            report.error(f"{label}: LiteDB {bad.get('version')} is not a published NuGet package")
        return
    provenance.check_commit(bad, label, report, dev_ref, offline)


def select(base, head, entries):
    """Entries added or changed between the revisions, or whose repro changed.

    A change to the proving harness itself re-proves every entry."""
    base_tree, report = common.Tree(base), common.Report("")
    old = {entry.get("repro"): entry for entry in load(base_tree, report)}
    changes = common.changed_files(base, head.rev)
    if any(path.startswith(HARNESS) for path in changes):
        return list(entries)
    selected = []
    for entry in entries:
        folder = f"{REPROS}/{entry.get('repro')}/"
        if old.get(entry.get("repro")) != entry or any(path.startswith(folder) for path in changes):
            selected.append(entry)
    return selected


def new_proofs(base, entries):
    """Proofs this change adds, or whose known-bad pin it changes (a bug fix's own proof)."""
    old = {entry.get("repro"): entry for entry in load(common.Tree(base), common.Report(""))}
    return [entry for entry in entries
            if entry.get("repro") not in old or old[entry.get("repro")].get("knownBad") != entry.get("knownBad")]


def normalize_version(version):
    """NuGet's normalized form: lowercase, no build metadata, no leading zeros, no zero 4th part."""
    core, _, label = str(version).lower().split("+")[0].partition("-")
    parts = [str(int(part)) if part.isdigit() else part for part in core.split(".")]
    if len(parts) == 4 and parts[3] == "0":
        parts = parts[:3]
    return ".".join(parts) + (f"-{label}" if label else "")


def published_versions():
    with urllib.request.urlopen(NUGET_INDEX, timeout=30) as response:
        return json.load(response).get("versions", [])


def parse_known_bad(value, reason):
    """'latest', 'package:5.0.21', 'dev-commit:<sha>', 'pr-commit:<sha>@<pr>' or 'pr-commit:<sha>@<owner>/<name>#<pr>'."""
    usage = ("--known-bad must be latest, package:<version>, dev-commit:<sha>, pr-commit:<sha>@<pr> "
             "or pr-commit:<sha>@<owner>/<name>#<pr> (a fork's PR)")
    if value == "latest":
        return {"kind": "package", "version": published_versions()[-1]}
    kind, _, reference = value.partition(":")
    if kind == "package" and reference:
        return {"kind": "package", "version": reference}
    if kind in ("dev-commit", "pr-commit"):
        commit, _, origin = reference.partition("@")
        repository, _, pr = origin.rpartition("#") if "#" in origin else ("", "", origin)
        if kind == "pr-commit" and (not pr.isdigit() or (repository and not provenance.valid_repository(repository))):
            raise SystemExit(usage)
        source = {"kind": kind, "commit": commit, "pr": int(pr) if pr.isdigit() else None}
        if repository:
            source["repository"] = repository
        if kind == "pr-commit" and provenance.SHA.match(commit):
            provenance.ensure_commit(source)  # a fork's commit may not be local yet
        try:
            commit = common.git("rev-parse", "--verify", "--quiet", f"{commit}^{{commit}}").strip()
        except subprocess.CalledProcessError:
            raise SystemExit(f"{usage}; {commit!r} is not a commit in this clone") from None
        bad = {"kind": kind, "commit": commit, "reason": reason or ""}
        if kind == "pr-commit":
            bad["pr"] = int(pr)
            if repository:
                bad["repository"] = repository
        return bad
    raise SystemExit(usage)


def write_ledger(entries):
    """Rewrite the ledger in its compact style (one line per known-bad state)."""
    path = Path(common.repo_root()) / LEDGER
    data = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"schemaVersion": 1}
    blocks = []
    for entry in entries:
        guard = ",\n".join(f"        {json.dumps(item)}" for item in entry.get("permanentGuard", []))
        blocks.append(f'    {{\n      "repro": {json.dumps(entry["repro"])},\n'
                      f'      "knownBad": {json.dumps(entry["knownBad"])},\n'
                      f'      "permanentGuard": [' + (f"\n{guard}\n      " if guard else "") + "]\n    }")
    header = "".join(f'  {json.dumps(key)}: {json.dumps(value)},\n' for key, value in data.items() if key != "proofs")
    path.write_text("{\n" + header + '  "proofs": [\n' + ",\n".join(blocks) + "\n  ]\n}\n", encoding="utf-8")


def matrix_item(tree, entry):
    manifest = _json(tree, f"{REPROS}/{entry['repro']}/repro.json") or {}
    supports = [str(value).lower() for value in manifest.get("supports") or ["any"]]
    bad = entry["knownBad"]
    return {"repro": entry["repro"], "os": "windows-latest" if supports == ["windows"] else "ubuntu-latest",
            "kind": bad["kind"], "commit": bad.get("commit", ""), "version": known_bad_version(bad),
            "pr": bad.get("pr", ""), "repository": bad.get("repository", "")}


def pack_known_bad(commit, feed, pr=None, repository=None):
    """Build LiteDB at a known-bad commit into a local feed as 0.0.0-knownbad.<commit>.

    A pr-commit (pr, and repository for a fork's PR) is fetched first when the
    clone lacks it. The commit's own build decides the version it stamps (older
    GitVersion setups ignore overrides), so the produced package is re-versioned
    afterwards."""
    if pr:
        source = {"kind": "pr-commit", "commit": commit, "pr": int(pr)}
        if repository:
            source["repository"] = repository
        if not provenance.ensure_commit(source):
            raise SystemExit(f"{commit} is not available after fetching {provenance.describe(source)}")
    version = known_bad_version({"kind": "dev-commit", "commit": commit})
    scratch = Path(tempfile.mkdtemp(prefix="litedb-known-bad-"))
    checkout, packed = scratch / "src", scratch / "packed"
    subprocess.run(["git", "worktree", "add", "--detach", str(checkout), commit], cwd=common.repo_root(), check=True)
    try:
        subprocess.run(["dotnet", "pack", str(checkout / "LiteDB" / "LiteDB.csproj"), "-c", "Release",
                        "-o", str(packed), "-p:TestingEnabled=false"], check=True)
        packages = [path for path in packed.glob("LiteDB.*.nupkg") if not path.name.endswith(".symbols.nupkg")]
        if len(packages) != 1:
            raise SystemExit(f"expected one LiteDB package from {commit}, found {[path.name for path in packages]}")
        Path(feed).mkdir(parents=True, exist_ok=True)
        _reversion(packages[0], version, Path(feed) / f"LiteDB.{version}.nupkg")
    finally:
        subprocess.run(["git", "worktree", "remove", "--force", str(checkout)], cwd=common.repo_root(), check=False)
    return version


def _reversion(source, version, target):
    with zipfile.ZipFile(source) as package, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as output:
        for item in package.infolist():
            content = package.read(item.filename)
            if item.filename.endswith(".nuspec"):
                content = re.sub(rb"<version>[^<]*</version>", f"<version>{version}</version>".encode(), content, 1)
            output.writestr(item, content)


def verify(report_path, repro, report, expected_version=None):
    """The run must show the known-bad variant reproducing and the candidate not reproducing."""
    data = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    entries = [item for item in data.get("Repros", data.get("repros", []))
               if str(item.get("Id", item.get("id"))) == repro]
    if len(entries) != 1:
        report.error(f"{report_path} has no single result for {repro}")
        return
    item = {key.lower(): value for key, value in entries[0].items()}
    package = {key.lower(): value for key, value in (item.get("package") or {}).items()}
    latest = {key.lower(): value for key, value in (item.get("latest") or {}).items()}
    if item.get("failed") or not package.get("met") or not latest.get("met"):
        report.error(f"{repro}: the run did not meet both expectations (failed={item.get('failed')})")
    if _name(package.get("expected"), OUTCOMES) not in ("Reproduce", "HardFail") or \
            _name(package.get("actual"), OUTCOMES) != _name(package.get("expected"), OUTCOMES):
        report.error(f"{repro}: the known-bad state did not fail as required (package {package})")
    if _name(latest.get("expected"), OUTCOMES) != "NoRepro" or _name(latest.get("actual"), OUTCOMES) != "NoRepro":
        report.error(f"{repro}: the candidate did not pass (latest {latest})")
    if package.get("useprojectreference") or not latest.get("useprojectreference", True):
        report.error(f"{repro}: variants are swapped; the known-bad state must be the package variant")
    # The repro reports the LiteDB it actually loaded; the runner's own table is not evidence.
    bad, candidate = _configuration(package), _configuration(latest)
    if expected_version and (bad.get("liteDBPackageVersion") != expected_version or bad.get("useProjectReference")):
        report.error(f"{repro}: the known-bad run reported {bad or 'no configuration'}, "
                     f"not package {expected_version}")
    if candidate.get("useProjectReference") is not True:
        report.error(f"{repro}: the candidate run reported {candidate or 'no configuration'}, not the source build")


def _configuration(variant):
    for line in variant.get("output") or []:
        try:
            event = json.loads(line.get("Text", line.get("text", "")))
        except (ValueError, AttributeError):
            continue
        if isinstance(event, dict) and event.get("type") == "configuration":
            return event.get("payload") or {}
    return {}


def _name(value, names):
    return names[value] if isinstance(value, int) and 0 <= value < len(names) else str(value)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("validate", help="Validate the ledger (offline unless --provenance)")
    check.add_argument("--head", default="HEAD")
    check.add_argument("--base", help="With --provenance, verify only entries changed since this revision")
    check.add_argument("--provenance", action="store_true", help="Also verify packages and commits (network, git)")
    check.add_argument("--dev-ref", default="origin/dev")
    check.add_argument("--only", help="With --provenance, verify only this repro's proof")
    choose = commands.add_parser("select", help="Emit the proofs to run for a change as a matrix")
    choose.add_argument("--base", required=True)
    choose.add_argument("--head", default="HEAD")
    choose.add_argument("--only", help="Select this repro regardless of changes (manual re-proof)")
    choose.add_argument("--require-new-proof", action="store_true",
                        help=f"Fail unless the change adds at least {MIN_BUG_PROOFS} proof (bug-fix PRs)")
    choose.add_argument("--labels", help=f"The PR's labels as a JSON array; any of {', '.join(BUG_LABELS)} "
                                         "implies --require-new-proof")
    scaffold = commands.add_parser("new", help="Scaffold a repro and its proof entry for a bug fix")
    scaffold.add_argument("--id", required=True, help="Issue_<number>_<ShortName>")
    scaffold.add_argument("--issue", required=True, type=int)
    scaffold.add_argument("--title", required=True)
    scaffold.add_argument("--known-bad", default="latest",
                          help="latest (newest published package), package:<v>, dev-commit:<sha>, pr-commit:<sha>@<pr>, "
                               "pr-commit:<sha>@<owner>/<name>#<pr> (a fork's PR)")
    scaffold.add_argument("--reason", help="Why no published package can be used (commit states)")
    scaffold.add_argument("--guard", action="append", default=[],
                          help="Permanent regression test 'path#Method', 'fuzz:<target>' or a script (repeatable)")
    pack = commands.add_parser("pack-known-bad", help="Pack LiteDB at a commit into a local feed")
    pack.add_argument("--commit", required=True)
    pack.add_argument("--feed", required=True)
    pack.add_argument("--pr", help="The commit's originating PR (fetched when the commit is not local)")
    pack.add_argument("--repository", help="With --pr, the fork that hosts the PR as owner/name")
    check_run = commands.add_parser("verify", help="Verify a ReproRunner report proves the fix")
    check_run.add_argument("--report", required=True)
    check_run.add_argument("--repro", required=True)
    check_run.add_argument("--expect-version", help="The known-bad LiteDB version the package run must report")
    args = parser.parse_args(argv)
    try:
        bug_fix = args.command == "select" and (args.require_new_proof or is_bug_fix(parse_labels(args.labels)))
    except ValueError as error:
        parser.error(str(error))

    if args.command == "pack-known-bad":
        print(pack_known_bad(args.commit, args.feed, args.pr or None, args.repository or None))
        return 0
    if args.command == "new":
        bad = parse_known_bad(args.known_bad, args.reason)
        entries = load(common.Tree(common.WORKTREE), common.Report(""))
        entries.append(repro_scaffold.create(common.repo_root(), args.id, args.issue, args.title, bad,
                                             known_bad_version(bad), args.guard))
        write_ledger(entries)
        print(f"Created {REPROS}/{args.id} (known bad: {known_bad_version(bad)}). Write the reproduction in "
              f"Program.cs, name the permanent guard with --guard or in {LEDGER}, then run:\n"
              f"  dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- run {args.id}")
        return 0
    report = common.Report("Regression proof")
    if args.command == "verify":
        verify(args.report, args.repro, report, args.expect_version)
        return report.finish()
    head = common.Tree(args.head)
    entries = load(head, report)
    repros = [entry.get("repro") for entry in entries]
    for duplicate in sorted({value for value in repros if repros.count(value) > 1}, key=str):
        report.error(f"{duplicate} has more than one regression proof", LEDGER)
    for entry in entries:
        validate_entry(head, entry, report)
    if args.command == "validate":
        checked = select(args.base, head, entries) if args.base else entries
        if args.only:
            checked = [entry for entry in entries if entry.get("repro") == args.only]
            if not checked:
                report.error(f"No regression proof for {args.only} in {LEDGER}", LEDGER)
        for entry in checked if args.provenance else []:
            check_provenance(entry, report, args.dev_ref)
        report.section(f"{len(entries)} regression proofs in `{LEDGER}`.")
        return report.finish()
    chosen = [entry for entry in entries if entry.get("repro") == args.only] if args.only \
        else select(args.base, head, entries)
    if args.only and not chosen:
        report.error(f"No regression proof for {args.only} in {LEDGER}", LEDGER)
    if bug_fix and len(new_proofs(args.base, entries)) < MIN_BUG_PROOFS:
        report.error(f"A bug-fix PR (labelled {' or '.join(BUG_LABELS)}) must add at least {MIN_BUG_PROOFS} "
                     "regression proof: a ReproRunner repro "
                     "that fails on a real known-bad state and passes at the PR head. Scaffold one with "
                     "`python .github/scripts/regression_proof.py new --id Issue_<n>_<Name> --issue <n> "
                     "--title <title>` (see docs/rules/safety-evidence.md#regression-proofs).")
    matrix = [] if report.errors else [matrix_item(head, entry) for entry in chosen]  # errors prove nothing
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"matrix={json.dumps({'include': matrix})}\ncount={len(matrix)}\n")
    report.section("Proofs to run: " + (", ".join(f"`{item['repro']}` (known bad: {item['kind']} "
                                                   f"{item['commit'] or item['version']})" for item in matrix)
                                        or "none (no regression proof added or changed)"))
    return report.finish()


if __name__ == "__main__":
    sys.exit(main())
