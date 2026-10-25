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
print(json.dumps(results, indent=2))
