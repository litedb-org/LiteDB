"""Exact expanded contract and unaccepted seed checks for the one-off v11 audit."""
from datetime import datetime, timezone
import json


CONTRACT_FILES = {
    'scripts/bugfix/issues.json',
    'scripts/bugfix/corepair_contract_assertions.py',
    'scripts/bugfix/test_auto_id_corepair.py',
    'scripts/bugfix/test_string_id_corepair.py',
    'scripts/bugfix/fixtures/issue-1002-2811-2590-corepair.json',
}


def validate_contract_change(old_raw, new_raw, fixture_raw):
    import handoff as h
    old, new = json.loads(old_raw), json.loads(new_raw)
    h.require(set(old) == set(new) and set(old['issues']) == set(new['issues']), 'Contract inventory changed')
    for key in old:
        if key != 'issues': h.require(old[key] == new[key], 'Manifest metadata changed')
    for issue in old['issues']:
        if issue != '1002': h.require(old['issues'][issue] == new['issues'][issue], 'Unrelated contract changed')
    before, after = old['issues']['1002'], new['issues']['1002']
    fixture = json.loads(fixture_raw)
    h.require(fixture['superseded_contract'] == before and fixture['contract'] == after, 'Reviewed fixture contract differs')
    for case in after['regressions'][9:] + after['controls'][2:]:
        raw = fixture['cases'][case['name']]
        h.require(case['test_id'] == raw['test_id'] and all(item['outcome'] == raw['outcome'] for item in case['baseline_by_environment'].values()), 'New case evidence differs')
        if raw['outcome'] == 'Failed':
            h.require(all(value['sha256'] == h.sha256(raw['canonical_failure'].encode('utf-8')) for value in case['failure_classifications'].values()), 'New canonical failure digest differs')
    allowed = {'regressions', 'controls', 'filter', 'frozen_test_blobs', 'review_requirements'}
    h.require(set(before) == set(after), 'Contract fields changed')
    for key in before:
        if key not in allowed: h.require(before[key] == after[key], 'Existing contract requirement changed: ' + key)
    h.require(after['regressions'][:9] == before['regressions'] and len(after['regressions']) == 13
              and after['controls'][:2] == before['controls'] and len(after['controls']) == 3, 'Old cases changed or additions differ')
    expected = {'LiteDB.Tests.Issues.Issue2590_Tests.Empty_string_ids_are_addressable_or_rejected_before_any_write(path: ' + path + ')'
                for path in ('InsertOne', 'InsertMany', 'UpsertOne', 'UpsertMany')}
    h.require({case['name'] for case in after['regressions'][9:]} == expected, 'Wrong behavioral co-repair cases')
    h.require(after['controls'][2]['name'] == 'LiteDB.Tests.Database.AutoId_Tests.AutoId_BsonDocument', 'Wrong new control')
    suffix = '|FullyQualifiedName~LiteDB.Tests.Issues.Issue2590_|FullyQualifiedName=LiteDB.Tests.Database.AutoId_Tests.AutoId_BsonDocument'
    h.require(after['filter'] == before['filter'] + suffix, 'Filter expansion differs')
    path = 'LiteDB.Tests/Issues/Issue2590_Tests.cs'
    blob = h.github(h.REPO, f'contents/{path}?ref={h.SOURCE}')['sha']
    h.require(after['frozen_test_blobs'] == {**before['frozen_test_blobs'], path: blob}, 'Frozen co-repair blob changed')
    h.require(set(after['review_requirements']) == set(before['review_requirements']), 'Review roles changed')
    for role, notes in before['review_requirements'].items():
        h.require(after['review_requirements'][role][:len(notes)] == notes, 'Prior review obligations removed or rewritten')
    notes = '\n'.join(after['review_requirements']['behavior'])
    h.require(all(token in notes for token in (h.CANDIDATE, h.BASE, '35075342165', '35077204440',
                                               'LiteDB/Client/Database/Collections/Insert.cs', 'Unaccepted implementation seed only')),
              'Explicit unaccepted seed instructions are incomplete')


def validate_candidate(campaign):
    import handoff as h
    candidate = h.github(h.REPO, f'git/commits/{h.CANDIDATE}')
    h.require(candidate['sha'] == h.CANDIDATE and [p['sha'] for p in candidate['parents']] == [h.BASE]
              and candidate['tree']['sha'] == '190435030337c4d77a9f076872557bed9e7c641c', 'Unaccepted candidate identity changed')
    ref = h.github(h.REPO, 'git/ref/heads/fix/issue-1002-hosted-v10-1002-a1')
    h.require(ref['object']['sha'] == h.CANDIDATE, 'Seed branch moved')
    comparison = h.github(h.REPO, f'compare/{h.BASE}...{h.CANDIDATE}')
    h.require([f['filename'] for f in comparison['files']] == ['LiteDB/Client/Database/Collections/Insert.cs'], 'Seed scope changed')
    broad = campaign['evidence']['broad']
    h.require(broad['run_id'] == 35077204440 and broad['outcome'] == 'inconclusive'
              and broad['candidate_sha'] == h.CANDIDATE and broad['workflow_sha'] == h.OLD, 'Old CI disposition changed')
    expected_name = 'LiteDB.Tests.Issues.Issue2590_Tests.Empty_string_ids_are_addressable_or_rejected_before_any_write(path: InsertOne)'
    h.require(len(broad['diagnostics']) == 2, 'Unexpected old CI diagnostics')
    for lane in broad['diagnostics']:
        h.require(lane['outcome'] == 'inconclusive' and len(lane['diagnostics']) == 1, 'Other unresolved CI failures')
        item = lane['diagnostics'][0]
        h.require(item['name'] == expected_name and item['baseline_failure_sha256'] == 'a5cb127e80308bbf3f4f3782a83988986973b0e73c564001af1e594e30427fa7'
                  and item['candidate_failure_sha256'] == 'd27dd3fd3a45430331874bb58efe26ed78a78850b53e8950a46359e174ac2e43', 'Old classification drift changed')
    artifacts = h.github(h.REPO, 'actions/runs/35077204440/artifacts?per_page=100')
    h.require(artifacts['total_count'] <= 100 and len(artifacts['artifacts']) == artifacts['total_count'], 'Incomplete old CI artifacts')
    for lane in broad['failed_matrix']:
        matching = [a for a in artifacts['artifacts'] if a['name'] == lane['artifact'] and not a['expired']]
        h.require(len(matching) == 1 and h.sha256(h.download(h.REPO, matching[0])) == lane['artifact_sha256'], 'Original CI artifact digest changed')


def resolved_requests(campaigns):
    import handoff as h
    requests = {campaign['campaign'] + '/' + key: request for campaign in campaigns
                for key, request in campaign['orchestration']['requests'].items()}
    h.require(set(requests) == {'hosted-v10-1002/baseline-0-0', 'hosted-v10-1002/fix-1-0',
                                'hosted-v10-1002/broad-1-0', 'hosted-v10-2802/baseline-0-0'}, 'Old request inventory changed')
    runner = h.Runs(h.REPO, 'automation/bugfix-runtime-v10', h.OLD, {}, lambda: None)
    resolved = {}
    for key, request in requests.items():
        found = runner._find(request['workflow'], request['request_id'])
        h.require(found is not None, 'Old request cannot be uniquely resolved')
        run_id = found['id']
        h.require(request.get('run_id', run_id) == run_id, 'Recorded old run ID changed')
        evidence = h.completed(run_id)
        h.require(h.match_run([evidence], request['request_id'], h.OLD, 'automation/bugfix-runtime-v10') == evidence
                  and evidence.get('path') == '.github/workflows/' + request['workflow'], 'Old run provenance mismatch')
        if key.startswith('hosted-v10-2802/'):
            h.require(request['request_id'] == 'bf-04adb2dcca724f9a8455ea17c20ba8cf' and run_id == 35078119652
                      and evidence['conclusion'] == 'success', 'Pending #2802 baseline resolved incorrectly')
        if key.endswith('broad-1-0'):
            h.require(run_id == 35077204440 and evidence['conclusion'] == 'failure', 'Blocked CI run changed')
        resolved[key] = {'original_request': request, 'resolved_run': evidence}
    since = datetime.fromtimestamp(min(r['started_at'] for r in requests.values()), timezone.utc).strftime('%Y-%m-%d')
    seen = set()
    for workflow in ('bugfix-fix.lock.yml', 'bugfix-check.yml', 'bugfix-validate.lock.yml'):
        listing = h.github(h.REPO, f'actions/workflows/{workflow}/runs?event=workflow_dispatch&branch=automation%2Fbugfix-runtime-v10&created=%3E%3D{since}&per_page=100')
        h.require(listing.get('total_count', 101) <= 100 and len(listing['workflow_runs']) == listing['total_count'], 'Incomplete old-run inventory')
        for run in listing['workflow_runs']:
            h.require(run.get('head_sha') == h.OLD and run.get('head_branch') == 'automation/bugfix-runtime-v10', 'Old runtime branch changed')
            h.require(run['id'] in {v['resolved_run']['id'] for v in resolved.values()} and run['id'] not in seen, 'Orphan or duplicate old run')
            seen.add(run['id'])
    h.require(seen == {v['resolved_run']['id'] for v in resolved.values()}, 'Old request inventory incomplete')
    return resolved
