#!/usr/bin/env python3
"""Bounded SC model of cached-state admission, destructive work, and reader protection.

Atomic publications and full fences are assumed sequentially consistent. This is
an executable protocol model, not a proof of CLR/CPU/filesystem memory ordering.
The physical fence abstracts structural/reset/incarnation changes. The separate
reuse model represents append refresh's obligation to reject a rewritten prefix.
"""
import itertools
import json


def schedules(reader, writer):
    for positions in itertools.combinations(range(len(reader) + len(writer)), len(reader)):
        positions = set(positions)
        r, w = iter(reader), iter(writer)
        yield [next(r) if index in positions else next(w) for index in range(len(reader) + len(writer))]


def admission(order, mutant):
    epoch = 0
    before = None
    lease = False
    observed = False
    invalidated = False
    accepted = False
    aborted = False
    for event in order:
        if event == 'read-status':
            before = epoch
            aborted = epoch != 0
        elif event == 'lease' and not aborted:
            lease = True
        elif event == 'recheck' and not aborted:
            accepted = mutant == 'skip-recheck' or (epoch == before and epoch % 2 == 0)
        elif event == 'read-data' and accepted and invalidated:
            return False
        elif event == 'release':
            lease = False
        elif event == 'begin' and mutant != 'unmarked-writer':
            epoch += 1
        elif event == 'scan':
            observed = lease
        elif event == 'rewrite':
            invalidated = mutant == 'ignore-lease' or not observed
        elif event == 'end' and mutant != 'unmarked-writer':
            epoch += 1
    return True


def refresh(order, ignore_reuse):
    epoch = 0
    before = None
    changed = False
    accepted = False
    for event in order:
        if event == 'read-status':
            before = epoch
        elif event == 'publish-reuse':
            epoch += 1
        elif event == 'rewrite-prefix':
            changed = True
        elif event == 'recheck':
            accepted = before == 0 and (ignore_reuse or epoch == before)
        elif event == 'expose-refreshed-index' and accepted and changed:
            return False
    return True


reader = ['read-status', 'lease', 'recheck', 'read-data', 'release']
writer = ['begin', 'scan', 'rewrite', 'end']
results = {}
for mutant in ['correct', 'skip-recheck', 'unmarked-writer', 'ignore-lease']:
    # Include writer death before each boundary; an odd publication remains odd.
    orders = [order for end in range(len(writer) + 1) for order in schedules(reader, writer[:end])]
    failures = [order for order in orders if not admission(order, mutant)]
    assert bool(failures) == (mutant != 'correct'), (mutant, failures[:1])
    results[mutant] = dict(schedules=len(orders), counterexamples=len(failures), example=failures[:1])
# Readers expose a refreshed version only after validating the entire read interval;
# reuse after validation is excluded by the already published version lease.
orders = [order for order in schedules(['read-status', 'recheck', 'expose-refreshed-index'],
                                      ['publish-reuse', 'rewrite-prefix'])
          if order.index('rewrite-prefix') < order.index('recheck')]
for ignore in [False, True]:
    failures = [order for order in orders if not refresh(order, ignore)]
    assert bool(failures) == ignore
    results['ignore-reuse' if ignore else 'refresh-correct'] = dict(
        schedules=len(orders), counterexamples=len(failures), example=failures[:1])
# Bootstrap interleavings: DMS release on death does not make Ready durable.
# Each surviving shared holder forbids resetting the authority generation.
orders = []
for cut in range(1, 4):
    initializer = ['exclusive', 'ready', 'downgrade'][:cut] + ['death']
    # A surviving peer is modeled separately below: once attached it holds DMS,
    # so initializer death cannot remove that peer's shared lock.
    for order in schedules(initializer, ['attach', 'reset', 'use']):
        if order.index('exclusive') != 0:
            continue
        # Hold peer DMS until use: a reset after attach cannot be exclusive.
        orders.append(order)
# The death above must retain a live peer's shared ownership.
def with_peer(order, mutant):
    peer = False
    generation = 0
    ready = False
    exclusive = False
    holder = False
    accepted = None
    for event in order:
        if event == 'exclusive':
            exclusive, generation, ready = True, generation + 1, False
        elif event == 'ready':
            ready = True
        elif event == 'downgrade':
            exclusive, holder = False, True
        elif event == 'attach' and not exclusive and holder and ready:
            peer, accepted = True, generation
        elif event == 'death':
            exclusive, holder = False, False
        elif event == 'reset' and (mutant or not (exclusive or holder or peer)):
            generation, ready = generation + 1, False
        elif event == 'use' and accepted is not None and (generation != accepted or not ready):
            return False
    return True
for mutant in [False, True]:
    failures = [order for order in orders if not with_peer(order, mutant)]
    assert bool(failures) == mutant
    results['bootstrap-reset-live' if mutant else 'bootstrap-correct'] = dict(
        schedules=len(orders), counterexamples=len(failures), example=failures[:1])
# Exact-version table versus an aggregate count/minimum. Duplicate releases and
# irregular reuse must not erase a distinct surviving floor or a successor lease.
exact_cases = 0
aggregate_failures = 0
stale_failures = 0
for release_order in itertools.permutations(range(4)):
    versions = [0, 4, 9, 4]
    live = set(range(4))
    for released in release_order:
        live.remove(released)
        expected = {versions[index] for index in live}
        records = {version for version in versions if any(versions[index] == version for index in live)}
        assert records == expected
        if expected and {min(expected)} != expected:
            aggregate_failures += 1
        # Reusing a record with a new lease must survive double/stale completion.
        successor = (released, 'next-incarnation')
        completed = {(released, 'old-incarnation')}
        assert successor not in completed
        stale_failures += int(successor[0] == released)
        exact_cases += 1
assert aggregate_failures and stale_failures
results['exact-version-release-reuse'] = dict(schedules=exact_cases, counterexamples=0,
    aggregate_minimum_counterexamples=aggregate_failures, untagged_release_counterexamples=stale_failures)
print(json.dumps(results, indent=2))
