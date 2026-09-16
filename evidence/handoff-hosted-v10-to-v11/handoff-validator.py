"""One-off, audit-only v10-to-v11 handoff. Never edits old campaigns or dispatches."""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.github/bugfix'))
from artifacts import download
from integrate_storage import IntegrationStore, encoded
from passing import digest, passing_cases
from queue_support import ancestor
from runs import Runs, match_run
from state import SHA, require
from storage import github
from sweep_plan import validate

REPO = 'litedb-org/LiteDB'
OLD = '6b9cf0507ae2f341834682da1b1d268946548931'
SCHEDULER = '5a823c25057fa4eaffb8cdfbd546ae3f5c360f37'
BASE = 'bbb0253bc06324f0bb14a21a727a37e8c7f2b213'
SOURCE = 'dd937719f7eee53c512f50ac604cab639bf42a4c'
CANDIDATE = '33fbf17558062276c223946b66aa3f7c01810ad6'
PREFIX = 'evidence/handoff-hosted-v10-to-v11/'
AUTHORIZATION = ('Authorized expanded #1002/#2811/#2590 contract task with fresh maximum three candidates. '
                 'V10 attempt1, unaccepted candidate33fb, inconclusive CI35077204440, and all prior lifecycle '
                 'obligations remain recorded. This is no retry under the old contract or acceptance of its patch.')


def sha256(raw):
    return hashlib.sha256(raw).hexdigest()


def raw_file(sha, path):
    item = github(REPO, f'contents/{path}?ref={sha}')
    require(item.get('type') == 'file' and item.get('encoding') == 'base64', 'Missing ordinary Git file')
    raw = base64.b64decode(''.join(item['content'].split()), validate=True)
    require(len(raw) == item['size'] and len(raw) <= 8 * 1024 * 1024, 'Invalid file size')
    require(hashlib.sha1(f'blob {len(raw)}\0'.encode() + raw).hexdigest() == item['sha'], 'Git blob identity mismatch')
    return raw


def runtime_diff(new_sha):
    def git(*args):
        result = subprocess.run(['git', *args], cwd=ROOT, capture_output=True, check=True)
        return result.stdout
    for sha in (OLD, new_sha):
        require(git('rev-parse', sha + '^{commit}').decode().strip() == sha, 'Missing immutable local commit')
    require(git('merge-base', OLD, new_sha).decode().strip() == OLD, 'Old runtime is not an ancestor of new runtime')
    raw = git('diff', '--raw', '--no-abbrev', '--no-renames', OLD, new_sha, '--').decode('utf-8')
    files = []
    for line in raw.splitlines():
        info, path = line.split('\t')
        old_mode, new_mode, old_blob, new_blob, status = info[1:].split()
        from transition import CONTRACT_FILES
        require(path in CONTRACT_FILES or path.startswith(('.github/bugfix/', '.github/scripts/', 'docs/Tasks/Wholesale-Bugfix/'))
                or path in ('.github/workflows/bugfix-fix.md', '.github/workflows/bugfix-fix.lock.yml',
                            '.github/workflows/bugfix-validate.md', '.github/workflows/bugfix-validate.lock.yml'),
                f'Outside reviewed infrastructure boundary: {path}')
        require(old_mode in ('000000', '100644') and new_mode in ('000000', '100644'), 'Unexpected file mode')
        require(status in ('A', 'M'), 'Only reviewed additions/modifications are allowed')
        files.append(dict(path=path, old_blob=old_blob, new_blob=new_blob, old_mode=old_mode, new_mode=new_mode, status=status))
    require(files, 'No new runtime changes')
    patch = git('diff', '--binary', '--full-index', '--no-renames', '--no-ext-diff', '--no-textconv',
                '--no-color', '--src-prefix=a/', '--dst-prefix=b/', OLD, new_sha, '--')
    return dict(old_sha=OLD, new_sha=new_sha, files=files, patch_sha256=sha256(patch)), patch


def completed(run_id):
    value = github(REPO, f'actions/runs/{run_id}')
    require(value.get('id') == run_id and value.get('status') == 'completed', 'Run remains active or identity changed')
    return value


def controls():
    for name, expected in [('BUGFIX_SWEEP_ENABLED', 'false'), ('BUGFIX_SCHEDULER_SHA', SCHEDULER),
                           ('BUGFIX_ACTIVE_SWEEP', 'hosted-v10-main')]:
        require(github(REPO, f'actions/variables/{name}').get('value') == expected, f'Unsafe scheduler control: {name}')
    # A disabled variable prevents work, but queued/running host jobs must also drain before audit.
    for workflow in ('bugfix-sweep.yml', 'bugfix-fix.lock.yml', 'bugfix-check.yml', 'bugfix-validate.lock.yml'):
        for status in ('queued', 'in_progress', 'waiting', 'pending', 'requested'):
            result = github(REPO, f'actions/workflows/{workflow}/runs?status={status}&per_page=100')
            require(result.get('total_count') == 0, 'A hosted scheduler or child run has not drained')
    require(github(REPO, 'git/ref/heads/integration/bugfixes')['object']['sha'] == BASE, 'Integration base moved')


def resolve_requests(campaigns):
    from transition import resolved_requests
    return resolved_requests(campaigns)


def preview(args):
    require(SHA.fullmatch(args.new_sha) and args.new_sha != OLD and SHA.fullmatch(args.expected_state_sha), 'Full immutable SHAs required')
    require(args.new_ref == 'automation/bugfix-runtime-v11', 'Unexpected new runtime ref')
    store = IntegrationStore(REPO)
    require(store.store.current_sha() == args.expected_state_sha, 'Controller state moved')
    controls()
    tree = github(REPO, f'git/trees/{args.expected_state_sha}?recursive=1')
    require(not tree.get('truncated') and not any(item['path'].startswith(PREFIX.rstrip('/')) for item in tree['tree']), 'Handoff audit prefix already exists or inventory is truncated')
    require(github(REPO, f'git/ref/heads/{args.new_ref}')['object']['sha'] == args.new_sha, 'New runtime ref moved')
    old_contract = raw_file(OLD, 'scripts/bugfix/issues.json')
    new_contract = raw_file(args.new_sha, 'scripts/bugfix/issues.json')
    from transition import validate_contract_change, validate_candidate
    validate_contract_change(old_contract, new_contract, raw_file(args.new_sha, 'scripts/bugfix/fixtures/issue-1002-2811-2590-corepair.json'))
    parent_audit = raw_file('a225d1cdbb784209374d723d6d85751475be9fae', 'evidence/handoff-hosted-v9-to-v10/audit.json')
    require(json.loads(parent_audit)['new_runtime_sha'] == OLD
            and ancestor(REPO, 'a225d1cdbb784209374d723d6d85751475be9fae', args.expected_state_sha), 'Parent transition lineage changed')
    contracts = json.loads(old_contract)['issues']
    sweep = store.read_at('sweep-hosted-v10-main', args.expected_state_sha)
    validate(sweep, 'hosted-v10-main', contracts)
    require(sweep['paused'] is True and sweep['phase'] == 'paused', 'Old sweep is not explicitly paused')
    require(sweep['specification']['workflow_sha'] == OLD and sweep['specification']['scheduler_sha'] == SCHEDULER
            and sweep['specification']['campaign_prefix'] == 'hosted-v10', 'Old sweep pins changed')
    for name in ('sweep-lock', 'integration-lock'):
        lock = store.read_at(name, args.expected_state_sha)
        require(lock and lock.get('active') is False, 'A controller lease is active or missing')
        if name == 'sweep-lock':
            owner = completed(lock['owner']['run_id'])
            require(owner['run_attempt'] == lock['owner']['run_attempt'] and owner['path'] == '.github/workflows/bugfix-sweep.yml', 'Lease owner changed')
    campaign = store.read_at('hosted-v10-1002', args.expected_state_sha)
    require(campaign['phase'] == 'blocked' and campaign['repair_attempts'] == 1 and campaign['candidate_sha'] == CANDIDATE
            and campaign['base_sha'] == BASE and campaign['workflow_sha'] == OLD and campaign['test_source_sha'] == SOURCE,
            'Old candidate, attempt or blocked disposition changed')
    require(campaign['reviews'] == {} and not campaign['orchestration'].get('review_reports'), 'Unexpected new review obligations')
    require(campaign['orchestration']['worker_retries'] == 0, 'Old worker retry history changed')
    validate_candidate(campaign)
    campaigns = [campaign]
    for issue in sweep['specification']['issues']:
        require(store.read_at(f'hosted-v11-{issue}', args.expected_state_sha) is None, 'Fresh campaign already exists')
        if issue != 1002:
            previous = store.read_at(f'hosted-v10-{issue}', args.expected_state_sha)
            if previous:
                require(issue == 2802 and previous['phase'] == 'baseline' and previous['candidate_sha'] is None
                        and previous['repair_attempts'] == 0 and previous['base_sha'] == BASE
                        and previous['workflow_sha'] == OLD and previous['test_source_sha'] == SOURCE,
                        'Another old campaign has correctness progress')
                campaigns.append(previous)
    require(store.read_at('sweep-hosted-v11-main', args.expected_state_sha) is None, 'Fresh sweep already exists')
    ledger = store.read_at('accepted-tests', args.expected_state_sha)
    cases = passing_cases(ledger, BASE, SOURCE, lambda c, b: ancestor(REPO, c, b))
    require(set(ledger['issues']) == {'2874', '2839', '2869', '1506'} and len(cases) == 18, 'Accepted ledger changed')
    require(digest(ledger) == campaign['passing_contract']['ledger_sha256']
            and digest(cases) == campaign['passing_contract']['cases_sha256'], 'Permanent passing snapshot changed')
    difference, patch = runtime_diff(args.new_sha)
    require(difference == json.loads(Path(args.approved_diff).read_text(encoding='utf-8')), 'Runtime diff lacks exact operator review')
    resolved = resolve_requests(campaigns)
    files = {PREFIX + 'runtime.diff': patch, PREFIX + 'approved-diff.json': encoded(difference),
             PREFIX + 'old-campaign.json': encoded(campaign), PREFIX + 'old-sweep.json': encoded(sweep),
             PREFIX + 'accepted-tests.json': encoded(ledger), PREFIX + 'old-test-contracts.json': old_contract,
             PREFIX + 'new-test-contracts.json': new_contract, PREFIX + 'parent-handoff-audit.json': parent_audit,
             PREFIX + 'reviewed-corepair-fixture.json': raw_file(args.new_sha, 'scripts/bugfix/fixtures/issue-1002-2811-2590-corepair.json')}
    for previous in campaigns:
        files[PREFIX + previous['campaign'] + '.json'] = encoded(previous)
    for entry in resolved.values():
        run_id = entry['resolved_run']['id']
        if run_id not in (35077204440, 35078119652):
            continue
        artifacts = github(REPO, f'actions/runs/{run_id}/artifacts?per_page=100')
        require(0 < artifacts['total_count'] <= 100 and len(artifacts['artifacts']) == artifacts['total_count'], 'Incomplete blocked CI artifacts')
        entry['artifacts'] = artifacts
        for artifact in artifacts['artifacts']:
            require(not artifact['expired'], 'Blocked CI artifact expired before durable handoff')
            raw = download(REPO, artifact)
            artifact['download_sha256'] = sha256(raw)
            artifact['download_size'] = len(raw)
    require(sum(map(len, files.values())) <= 256 * 1024 * 1024, 'Handoff archive exceeds 256 MiB')
    audit = dict(schema_version=1, action='fresh-runtime-handoff-audit-only', authorization=AUTHORIZATION,
                 expected_state_sha=args.expected_state_sha, old_runtime_sha=OLD, new_runtime_sha=args.new_sha,
                 new_runtime_ref=args.new_ref, scheduler_sha=SCHEDULER, base_sha=BASE, test_source_sha=SOURCE,
                 old_sweep='hosted-v10-main', new_sweep='hosted-v11-main', new_campaign_prefix='hosted-v11',
                 issues=sweep['specification']['issues'], permanent_tests=cases, requests=resolved,
                 files={name: sha256(raw) for name, raw in files.items()},
                 script_sha256=sha256(Path(__file__).read_bytes()),
                 parent_transition={'state_commit': 'a225d1cdbb784209374d723d6d85751475be9fae',
                                    'path': 'evidence/handoff-hosted-v9-to-v10/audit.json', 'sha256': sha256(parent_audit)},
                 prior_contract_attempts=1, new_contract_max_candidates=3, seed_candidate_sha=CANDIDATE,
                 seed_is_unaccepted=True, superseded_check_run=35077204440,
                 fresh_baseline_required=True, old_journals_unchanged=True,
                 limitation='Audit authorizes no dispatch, acceptance, integration, or changes to original evidence.')
    files[PREFIX + 'handoff-validator.py'] = Path(__file__).read_bytes()
    files[PREFIX + 'transition-validator.py'] = (Path(__file__).parent / 'transition.py').read_bytes()
    audit['files'] = {name: sha256(raw) for name, raw in files.items()}
    files[PREFIX + 'audit.json'] = encoded(audit)
    require(store.store.current_sha() == args.expected_state_sha, 'State changed during preview')
    return audit, files


def execute(args):
    audit, files = preview(args)
    if args.apply:
        # External controls are not covered by the data ref lease: recheck immediately before CAS.
        controls()
        require(github(REPO, f'git/ref/heads/{args.new_ref}')['object']['sha'] == args.new_sha, 'New runtime ref moved')
        result = IntegrationStore(REPO).commit(args.expected_state_sha, files,
            'Audit expanded v11 contract handoff\n\nPreserve blocked v10 candidate and attempt lineage, approved additive contract and drained requests before a fresh co-repair task.')
        store = IntegrationStore(REPO)
        retained = store.read_prefix_at(PREFIX.rstrip('/'), result)
        require(retained == {name[len(PREFIX):]: raw for name, raw in files.items()}, 'Committed audit readback differs')
        for name in ('hosted-v10-1002', 'sweep-hosted-v10-main', 'hosted-v10-2802', 'accepted-tests', 'sweep-lock', 'integration-lock'):
            require(store.read_at(name, result) == store.read_at(name, args.expected_state_sha), 'Original controller journal changed')
        commit = github(REPO, f'git/commits/{result}')
        require([p['sha'] for p in commit['parents']] == [args.expected_state_sha], 'Audit CAS parent differs')
        audit = {**audit, 'audit_state_commit': result}
    Path(args.output).write_bytes(encoded(audit))
    return audit


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--new-sha', required=True)
    parser.add_argument('--new-ref', required=True)
    parser.add_argument('--expected-state-sha', required=True)
    parser.add_argument('--approved-diff', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--apply', action='store_true')
    options = parser.parse_args()
    result = execute(options)
    print(json.dumps({'accepted': True, 'applied': options.apply, 'audit_state_commit': result.get('audit_state_commit')}))
