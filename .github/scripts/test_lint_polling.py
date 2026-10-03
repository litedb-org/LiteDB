import json
import unittest

import lint_polling as lint
from safety_fixtures import GitRepo, run_quietly

PATH = "LiteDB/Client/Shared/Waiter.cs"


def source(body):
    return f"namespace LiteDB\n{{\n    internal class Waiter\n    {{\n        void Run()\n        {{\n{body}\n        }}\n    }}\n}}\n"


class PollingLintTests(unittest.TestCase):
    def run_lint(self, before, after, path=PATH):
        with GitRepo() as repo:
            base = repo.commit({path: source(before)})
            repo.commit({path: source(after)})
            return run_quietly(lint.main, ["--base", base])

    def assert_fires(self, body, path=PATH):
        code, output = self.run_lint("", body, path)
        self.assertEqual(code, 1, output)
        self.assertIn("polling: <reason>", output)

    def assert_quiet(self, body, path=PATH):
        code, output = self.run_lint("", body, path)
        self.assertEqual(code, 0, output)

    def test_timed_wait_in_loop_condition_fires(self):
        self.assert_fires("            while (!_mutex.WaitOne(50)) { token.ThrowIfCancellationRequested(); }")

    def test_timed_wait_in_loop_body_fires(self):
        for wait in ("_gate.Wait(TimeSpan.FromMilliseconds(10));", "Monitor.Wait(_sync, 100);",
                     "Thread.Sleep(1);", "Task.Delay(5).Wait();", "SpinWait.SpinUntil(() => _done, 10);",
                     "spinner.SpinOnce();", "WaitHandle.WaitAny(handles, 25);", "Monitor.TryEnter(_sync, 5);"):
            with self.subTest(wait):
                self.assert_fires(f"            for (var i = 0; i < 3; i++)\n            {{\n                if (x)\n"
                                  f"                {{\n                    {wait}\n                }}\n            }}")

    def test_do_loop_and_braceless_loop_fire(self):
        self.assert_fires("            do\n            {\n                Thread.Sleep(1);\n            }\n"
                          "            while (!_done);")
        self.assert_fires("            while (!_done)\n                Thread.Sleep(1);")
        self.assert_fires("            do { } while (!_event.Wait(5));")

    def test_polling_comment_on_the_line_or_two_lines_above_silences(self):
        self.assert_quiet("            // polling: the native wait cannot observe cancellation\n"
                          "            while (!_mutex.WaitOne(50)) { }")
        self.assert_quiet("            while (!_mutex.WaitOne(50)) { } // polling: native wait is uncancellable")

    def test_reason_must_be_given_and_within_two_lines(self):
        self.assert_fires("            // polling:\n            while (!_mutex.WaitOne(50)) { }")
        self.assert_fires("            // polling: far away\n            var a = 1;\n            var b = 2;\n"
                          "            while (!_mutex.WaitOne(50)) { }")

    def test_blocking_waits_and_waits_outside_loops_are_not_polling(self):
        for body in ("            _mutex.WaitOne(50);",
                     "            while (!_done) { _mutex.WaitOne(); }",
                     "            while (!_done) { Monitor.Wait(_sync); }",
                     "            while (!_done) { _opened.Wait(token); }",
                     "            while (!_done) { _gate.Wait(Timeout.Infinite); }",
                     "            while (!_done) { _event.Wait(cancellationToken); }",
                     "            while (!_done) { Monitor.TryEnter(_sync, ref taken); }",
                     "            // while (!_mutex.WaitOne(50)) { }",
                     '            while (!_done) { Log("Thread.Sleep(1)"); }',
                     "            Action a = () => { Thread.Sleep(1); };"):
            with self.subTest(body):
                self.assert_quiet(body)

    def test_wait_graph_scope_is_not_a_wait_but_the_wait_it_wraps_is_judged(self):
        scope = ("            while (!_done)\n            {{\n#if DEBUG || TESTING\n"
                 "                using ({graph}.Wait(_graph, WaitBound.After(timeout), \"Waiter.Run\"))\n#endif\n"
                 "                {wait}\n            }}")
        for graph in ("WaitGraph", "LiteDB.Utils.WaitGraph"):
            with self.subTest(graph):
                self.assert_quiet(scope.format(graph=graph, wait="Monitor.Wait(_sync);"))
                self.assert_fires(scope.format(graph=graph, wait="_gate.Wait(10);"))

    def test_only_added_lines_in_the_library_are_judged(self):
        loop = "            while (!_mutex.WaitOne(50)) { }"
        code, output = self.run_lint(loop, loop + "\n            var unrelated = 1;")
        self.assertEqual(code, 0, output)
        self.assert_quiet(loop, path="LiteDB.Tests/Waiter.cs")

    def test_single_switch_makes_findings_advisory(self):
        loop = "            while (!_mutex.WaitOne(50)) { }"
        modes = ".github/safety/net-modes.json"
        for blocking, flags, expected in ((False, [], 0), (True, [], 1), (False, ["--blocking"], 1),
                                          (True, ["--advisory"], 0)):
            with self.subTest(blocking=blocking, flags=flags), GitRepo() as repo:
                switch = json.dumps({"blocking": blocking, "nets": {"lint-polling": "x"}})
                base = repo.commit({PATH: source(""), modes: switch})
                repo.commit({PATH: source(loop)})
                code, output = run_quietly(lint.main, ["--base", base, *flags])
                self.assertEqual(code, expected, output)
                self.assertIn("polling: <reason>", output)

    def test_in_loop_reads_enclosing_blocks(self):
        code = "void M() { while (a) { if (b) { X(); } } Y(); }"
        self.assertTrue(lint.in_loop(code, code.index("X(")))
        self.assertFalse(lint.in_loop(code, code.index("Y(")))


if __name__ == "__main__":
    unittest.main()
