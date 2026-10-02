import contextlib
import io
import json
import os
import tempfile
import unittest
from types import SimpleNamespace

import compare_contention as contention
from safety_fixtures import run_quietly

MS = 1_000_000  # ticks per millisecond at a 1 GHz Stopwatch frequency
FREQUENCY = 1_000_000_000


def row(worker, seq, ticket, arrival_ms, acquired_ms, released_ms):
    return [worker, seq, ticket, int(arrival_ms * MS), int(acquired_ms * MS), int(released_ms * MS)]


def alternating(processes=2, iterations=10, hold_ms=10.0, barge=0, gap_ms=0.0):
    """A fair FIFO hand-off; with `barge` the releaser re-acquires that many times first,
    with `gap_ms` a waiter takes that long to notice a release."""
    samples, now, seq, ticket = [], 0.0, [0] * processes, 1
    arrivals = {worker: 0.0 for worker in range(processes)}
    order = []
    for _ in range(iterations):
        for worker in range(processes):
            order += [worker] * (1 + barge if worker == 0 else 1)
    for worker in order:
        if seq[worker] >= iterations:
            continue
        acquired = now + gap_ms if arrivals[worker] < now else max(now, arrivals[worker])
        released = acquired + hold_ms
        samples.append(row(worker, seq[worker], ticket, arrivals[worker], acquired, released))
        arrivals[worker], now, ticket = released, released, ticket + 1
        seq[worker] += 1
    return samples


def quietly(function, *args):
    with contextlib.redirect_stdout(io.StringIO()):
        return function(*args)


def options(**overrides):
    values = dict(contention.DEFAULTS)
    values.update(overrides)
    return SimpleNamespace(**values)


def run(variant, samples, processes=2, think=0):
    return {"variant": variant, "processes": processes, "thinkUs": think, "frequency": FREQUENCY,
            "samples": samples, "passed": True}


class PercentileTests(unittest.TestCase):
    def test_nearest_rank(self):
        values = list(range(1, 101))
        self.assertEqual(contention.percentile(values, .5), 50)
        self.assertEqual(contention.percentile(values, .99), 99)
        self.assertEqual(contention.percentile(values, .29), 29)
        self.assertEqual(contention.percentile(values, 1.0), 100)

    def test_small_and_unsorted_samples(self):
        self.assertEqual(contention.percentile([5], .99), 5)
        self.assertEqual(contention.percentile([3, 1, 2], .5), 2)
        self.assertEqual(contention.percentile([3, 1, 2], .99), 3)
        self.assertEqual(contention.percentile([4, 1], 0.0), 1)

    def test_empty_is_an_error(self):
        with self.assertRaises(ValueError):
            contention.percentile([], .5)


class StarvationTests(unittest.TestCase):
    def test_fifo_hand_off_has_no_starvation(self):
        self.assertEqual(contention.starvation(alternating(processes=3)), (0, 0))

    def test_releaser_that_re_acquires_past_a_waiter_counts_once_per_acquisition(self):
        # Worker 0 releases at 10 and acquires again at 10 while worker 1 waits since 0.
        samples = [row(0, 0, 1, 0, 0, 10), row(0, 1, 2, 10, 10, 20), row(1, 0, 3, 0.5, 20, 30)]
        self.assertEqual(contention.starvation(samples), (1, 1))

    def test_one_acquisition_bypassing_two_waiters_counts_once_with_two_bypasses(self):
        samples = [row(0, 0, 1, 0, 0, 10), row(1, 0, 3, 1, 20, 30), row(2, 0, 4, 2, 30, 40),
                   row(0, 1, 2, 10, 10, 20)]
        self.assertEqual(contention.starvation(samples), (1, 2))

    def test_later_arrival_acquiring_after_the_waiter_is_not_starvation(self):
        samples = [row(0, 0, 1, 0, 0, 10), row(1, 0, 2, 1, 10, 20), row(0, 1, 3, 10, 20, 30)]
        self.assertEqual(contention.starvation(samples), (0, 0))

    def test_waiter_that_already_acquired_is_not_bypassed(self):
        # Worker 1 arrived first but acquired before worker 2 did: not waiting any more.
        samples = [row(1, 0, 1, 0, 0, 5), row(2, 0, 2, 1, 5, 10)]
        self.assertEqual(contention.starvation(samples), (0, 0))

    def test_grace_ignores_near_simultaneous_arrivals(self):
        samples = [row(0, 0, 1, 1.00, 1.05, 10), row(1, 0, 2, 1.02, 1.01, 2)]
        self.assertEqual(contention.starvation(samples, grace_ticks=0), (1, 1))
        self.assertEqual(contention.starvation(samples, grace_ticks=0.1 * MS), (0, 0))

    def test_equal_arrivals_are_not_ordered(self):
        samples = [row(0, 0, 1, 1, 1, 2), row(1, 0, 2, 1, 2, 3)]
        self.assertEqual(contention.starvation(samples), (0, 0))

    def test_barging_releaser_is_counted_on_every_bypass(self):
        samples = alternating(processes=2, iterations=10, barge=1)
        count, bypasses = contention.starvation(samples)
        self.assertGreater(count, 0)
        self.assertEqual(count, bypasses)

    def test_matches_quadratic_definition(self):
        samples = alternating(processes=4, iterations=12, barge=2)
        expected = sum(any(w[3] < e[3] and w[4] > e[4] for w in samples) for e in samples)
        pairs = sum(w[3] < e[3] and w[4] > e[4] for e in samples for w in samples)
        self.assertEqual(contention.starvation(samples), (expected, pairs))


class HandoffTests(unittest.TestCase):
    def test_gaps_follow_ownership_order_and_skip_uncontended_acquisitions(self):
        samples = [row(0, 0, 1, 0, 0, 10), row(1, 0, 2, 1, 12, 20),   # waited: gap 2 ms
                   row(0, 1, 3, 25, 25, 30)]                         # arrived after release: skipped
        self.assertEqual(contention.handoffs(samples), [2 * MS])

    def test_release_stamp_after_the_true_release_clamps_to_zero(self):
        samples = [row(0, 0, 1, 0, 0, 10), row(1, 0, 2, 1, 9.5, 20)]
        self.assertEqual(contention.handoffs(samples), [0])

    def test_polling_waiter_shows_in_the_median_gap(self):
        samples = alternating(iterations=20, gap_ms=5.0)
        self.assertEqual(contention.check_samples(samples, FREQUENCY, 2, 20), [])
        self.assertAlmostEqual(contention.metrics(samples, FREQUENCY)["handoffP50Ms"], 5.0)
        self.assertEqual(contention.metrics(alternating(iterations=20), FREQUENCY)["handoffP50Ms"], 0.0)


class MetricsTests(unittest.TestCase):
    def test_latency_and_rates(self):
        samples = [row(0, 0, 1, 0, 0, 10), row(0, 1, 2, 10, 10, 20), row(1, 0, 3, 0.5, 20, 30)]
        result = contention.metrics(samples, FREQUENCY)
        self.assertEqual(result["acquisitions"], 3)
        self.assertEqual(result["perProcess"], {"0": 2, "1": 1})
        self.assertAlmostEqual(result["maxWaitMs"], 19.5)
        self.assertAlmostEqual(result["p50Ms"], 0.0)
        self.assertEqual(result["starvationCount"], 1)
        self.assertAlmostEqual(result["starvationRate"], 1 / 3)

    def test_consistent_samples_have_no_problems(self):
        samples = alternating(processes=2, iterations=5)
        self.assertEqual(contention.check_samples(samples, FREQUENCY, 2, 5, bracket_ns=(0, 10**12)), [])

    def test_ticket_order_must_match_acquisition_order(self):
        samples = alternating(processes=2, iterations=3)
        samples[0][2], samples[1][2] = samples[1][2], samples[0][2]
        problems = contention.check_samples(samples, FREQUENCY, 2, 3)
        self.assertTrue(any("ticket order" in problem for problem in problems), problems)

    def test_lost_ticket_and_missing_sequence_are_reported(self):
        samples = alternating(processes=2, iterations=3)
        del samples[2]
        problems = contention.check_samples(samples, FREQUENCY, 2, 3)
        self.assertTrue(any("contiguous" in problem for problem in problems), problems)
        self.assertTrue(any("sequence" in problem for problem in problems), problems)

    def test_samples_outside_the_driver_clock_are_reported(self):
        samples = alternating(processes=2, iterations=3)
        problems = contention.check_samples(samples, FREQUENCY, 2, 3, bracket_ns=(5 * MS, 10**12))
        self.assertTrue(any("bracket" in problem for problem in problems), problems)


class DecisionTests(unittest.TestCase):
    BASE = {"p99Ms": 20.0, "starvationRate": 0.01, "handoffP50Ms": 0.4, "maxWaitMs": 60.0}

    def decide(self, p99, rate, handoff=0.4, max_wait=60.0, **overrides):
        head = {"p99Ms": p99, "starvationRate": rate, "handoffP50Ms": handoff, "maxWaitMs": max_wait}
        return contention.decide(self.BASE, head, options(**overrides))

    def test_p99_needs_both_the_factor_and_the_floor(self):
        self.assertEqual(self.decide(29.9, .01), [])        # below x1.5
        self.assertEqual(self.decide(31.0, .01, p99_floor_ms=12), [])  # x1.55 but only +11 ms
        self.assertEqual(len(self.decide(31.0, .01)), 1)    # x1.55 and +11 ms > 5 ms

    def test_floor_protects_a_fast_base(self):
        fast = {"p99Ms": 1.0, "starvationRate": 0.0, "handoffP50Ms": 0.4, "maxWaitMs": 2.0}

        def decide(p99):
            return contention.decide(fast, dict(fast, p99Ms=p99), options())
        self.assertEqual(decide(5.9), [])
        self.assertEqual(len(decide(6.1)), 1)

    def test_hand_off_needs_both_the_factor_and_the_floor(self):
        self.assertEqual(self.decide(20, .01, handoff=1.39), [])   # +0.99 ms
        self.assertEqual(self.decide(20, .01, handoff=1.5, handoff_factor=4), [])
        self.assertEqual(len(self.decide(20, .01, handoff=1.5)), 1)  # x3.75 and +1.1 ms

    def test_max_wait_needs_both_the_factor_and_the_floor(self):
        self.assertEqual(self.decide(20, .01, max_wait=179), [])              # x2.98
        self.assertEqual(self.decide(20, .01, max_wait=181, max_floor_ms=125), [])  # +121 ms
        self.assertEqual(len(self.decide(20, .01, max_wait=181)), 1)

    def test_starved_waiter_fails_on_max_wait_even_when_p99_falls(self):
        reasons = self.decide(2.0, .01, max_wait=3000)
        self.assertEqual(len(reasons), 1)
        self.assertIn("max wait", reasons[0])

    def test_starvation_rise_in_percentage_points(self):
        self.assertEqual(self.decide(20, .06), [])
        self.assertEqual(len(self.decide(20, .0601)), 1)
        self.assertEqual(self.decide(20, 0.0), [])           # an improvement never fails

    def test_compare_uses_medians_across_rounds(self):
        fair, barging = alternating(iterations=50), alternating(iterations=50, barge=1)
        base = [run("baseline", fair) for _ in range(5)]
        # One noisy head round does not move the median.
        head = [run("candidate", barging)] + [run("candidate", fair) for _ in range(4)]
        report = contention.common.Report("test")
        rows = quietly(contention.compare, base, head, report, options())
        self.assertEqual(report.errors, [])
        self.assertEqual(rows[0][-1], "pass")
        head = [run("candidate", barging) for _ in range(3)] + [run("candidate", fair) for _ in range(2)]
        report = contention.common.Report("test")
        quietly(contention.compare, base, head, report, options())
        self.assertTrue(any("starvation" in error for error in report.errors), report.errors)

    def test_too_few_rounds_fail(self):
        fair = alternating(iterations=5)
        report = contention.common.Report("test")
        quietly(contention.compare, [run("baseline", fair)], [run("candidate", fair)], report, options())
        self.assertTrue(any("rounds" in error for error in report.errors), report.errors)

    def test_cli_accepts_paired_results_and_rejects_unverified_runs(self):
        fair = alternating(iterations=20)
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "acquire.jsonl")
            with open(path, "w", encoding="utf-8") as handle:
                for variant in ("baseline", "candidate") * 5:
                    handle.write(json.dumps(run(variant, fair)) + "\n")
            self.assertEqual(run_quietly(contention.main, ["--results", path])[0], 0)
            self.assertEqual(run_quietly(contention.main, ["--base", path, "--head", path])[0], 0)
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(json.dumps(dict(run("candidate", fair), passed=False)) + "\n")
            self.assertEqual(run_quietly(contention.main, ["--results", path])[0], 1)


if __name__ == "__main__":
    unittest.main()
