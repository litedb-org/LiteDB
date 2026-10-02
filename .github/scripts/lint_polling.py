"""Wait-primitive lint: a timed wait, sleep or spin added inside a loop must say why it polls.

A loop around a timed wait turns a blocking handoff into a poll: waiters stop being
woken in arrival order, latency becomes a multiple of the poll interval and the
fairness a blocking wait gave is lost without any test failing. Polling is
sometimes right (an uncancellable native wait that must observe cancellation), so
the lint does not forbid it; it requires a `// polling: <reason>` comment on the
line or within the two lines above it, so the choice is visible in review.

Diff-based: only lines added since --base in LiteDB/ are judged. The loop is judged
from the head file: an enclosing while/for/foreach/do block, a brace-less loop
body, or the loop condition itself (`while (!mutex.WaitOne(50))`).
"""
import argparse
import re
import sys

import safety_common as common

SOURCE_ROOT = "LiteDB/"
NOT_A_TIMEOUT = r"(?!\s*\)|\s*(?:-1|Timeout\.Infinite\w*)\s*\)|(?i:\s*[\w.]*(?:token|ct)\s*\)))"
# The wait primitives, in one place. A wait whose only argument is a cancellation token or
# an infinite timeout blocks until signalled; every other argument makes it a timed wait.
PRIMITIVES = re.compile("|".join([
    r"\.WaitOne\s*\(" + NOT_A_TIMEOUT,                 # WaitHandle.WaitOne(<ms|TimeSpan>)
    r"\bWaitHandle\.Wait(?:Any|All)\s*\([^,()]+,",     # WaitAny/WaitAll(handles, <timeout>)
    r"\bMonitor\.Wait\s*\([^,()]+,",                   # Monitor.Wait(x, <timeout>)
    r"\bMonitor\.TryEnter\s*\([^,()]+,(?!\s*ref\b)",  # Monitor.TryEnter(x, <timeout>)
    r"(?<!Monitor)\.Wait\s*\(" + NOT_A_TIMEOUT,     # SemaphoreSlim/ManualResetEventSlim/Task.Wait(<n>)
    r"\bThread\.Sleep\s*\(",
    r"\bTask\.Delay\s*\(",                             # sync-over-async (or awaited) delays
    r"\bSpin(?:Wait|Until|Once)\b",                    # SpinWait, SpinWait.SpinUntil, SpinOnce
]))
LOOP_HEAD = re.compile(r"(?:\bwhile|\bfor|\bforeach)\s*\(\s*\Z|\bdo\s*\Z")
POLLING = re.compile(r"//\s*polling:\s*\S{3,}")


def in_loop(code, offset):
    """True when offset lies in a loop condition, a brace-less loop body or a loop block."""
    statement = max(code.rfind(char, 0, offset) for char in ";{}") + 1
    if re.match(r"\s*(?:while|for|foreach)\s*\(|\s*do\b", code[statement:offset]):
        return True
    depth, index = 0, offset
    while index > 0:
        index -= 1
        depth += {"}": 1, "{": -1}.get(code[index], 0)
        if depth < 0:  # an enclosing block opens here; what introduced it?
            depth, head = 0, code[:index].rstrip()
            if head.endswith(")"):
                opener = _open_paren(head)
                if LOOP_HEAD.search(head[:opener + 1]):
                    return True
            elif re.search(r"\bdo\Z", head):
                return True
    return False


def _open_paren(text):
    depth = 0
    for index in range(len(text) - 1, -1, -1):
        depth += {")": 1, "(": -1}.get(text[index], 0)
        if depth == 0:
            return index
    return 0


def check(base, head, report):
    tree, added, findings = common.Tree(head), {}, []
    for path, line, _ in common.added_lines(base, head, [SOURCE_ROOT]):
        if path.endswith(".cs"):
            added.setdefault(path, []).append(line)
    for path, numbers in sorted(added.items()):
        text = tree.read(path) or ""
        code, lines = common.blank_code(text), text.split("\n")
        starts = [0]
        for item in lines:
            starts.append(starts[-1] + len(item) + 1)
        for line in numbers:
            span = code[starts[line - 1]:starts[line] - 1]
            hit = next((match for match in PRIMITIVES.finditer(span)
                        if in_loop(code, starts[line - 1] + match.start())), None)
            if hit and not any(POLLING.search(item) for item in lines[max(0, line - 3):line]):
                findings.append((path, line, lines[line - 1].strip()))
                report.error(f"Timed wait/sleep/spin in a loop without '// polling: <reason>': "
                             f"{lines[line - 1].strip()}", path, line)
    tree.close()
    return findings


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", required=True, help="Base revision (PR base or merge-base)")
    parser.add_argument("--head", default="HEAD", help=f"Head revision or {common.WORKTREE}")
    common.add_mode_arguments(parser)
    args = parser.parse_args(argv)
    report = common.Report("Wait-primitive lint")
    findings = check(args.base, args.head, report)
    report.section(f"{len(findings)} polling site(s) without a reason." if findings
                   else "No unexplained polling loop added.")
    return report.finish(advisory=common.net_advisory("lint-polling", args.blocking))


if __name__ == "__main__":
    sys.exit(main())
