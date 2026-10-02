#!/usr/bin/env bash
# Historical wait-for graph overlay for PR #133 trees (see README.md).
#
# usage: run.sh <tree> <artifacts> <mode>
#   mode: generic | repro-row3 | repro-row4 | repro-row12 | repro-row13
# env:  WAITGRAPH_FAIL   rules that fail the run (default: self-wait,unbounded-cycle)
#       WAITGRAPH_FILTER override the test filter of the mode
#
# Applies graph-new-files.patch and sites-<commit>.patch to <tree> (HEAD must be one of the
# commits a sites patch exists for), builds LiteDB.Tests (net8.0) with TestingEnabled, runs the
# mode's tests with LITEDB_WAITGRAPH_REPORT, and exits 1 with "WAIT_FOR_CYCLE:<rule>" lines when the
# graph latched a finding of a failing rule. Test failures alone never make the run fire: the yell
# is the graph's verdict. Exit 2 = harness problem (no patch for this commit, apply/build failed).
set -u
tree=$(cd "$1" && pwd); artifacts=$2; mode=$3
here=$(cd "$(dirname "$0")" && pwd)
fail=${WAITGRAPH_FAIL:-self-wait,unbounded-cycle}
mkdir -p "$artifacts"
cd "$tree" || exit 2

head=$(git rev-parse HEAD)
patch=""
for candidate in "$here"/sites-*.patch; do
    sha=$(basename "$candidate" .patch); sha=${sha#sites-}
    case "$head" in "$sha"*) patch=$candidate ;; esac
done
if [ -z "$patch" ]; then echo "no sites patch for $head" >&2; exit 2; fi
if [ ! -f LiteDB/Utils/WaitGraph.cs ]; then git apply --whitespace=nowarn "$here/graph-new-files.patch" || exit 2; fi
git apply --whitespace=nowarn "$patch" || exit 2

copy_fix_test() { # <fix commit> <path>: the fix's own test, when the tree does not have it yet
    [ -f "$2" ] || git show "$1:$2" > "$2" || exit 2
}
case "$mode" in
    generic)
        filter="FullyQualifiedName~TransactionHandle|FullyQualifiedName~Shared" ;;
    repro-row3)
        copy_fix_test 2124767a7467f8c854f2b2dd1a804e52826a7ef5 LiteDB.Tests/Engine/TransactionHandleMaintenanceProgress_Tests.cs
        filter="FullyQualifiedName~TransactionHandleMaintenanceProgress_Tests" ;;
    repro-row4)
        copy_fix_test e28612aa539504f8faf1d32008e7d267d8c7be43 LiteDB.Tests/Engine/TransactionHandleSharedNested_Tests.cs
        cp "$here/repro/WaitGraphRepro_Row4Hang_Tests.cs" LiteDB.Tests/Engine/
        filter="FullyQualifiedName~TransactionHandleSharedNested_Tests|FullyQualifiedName~WaitGraphRepro_Row4" ;;
    repro-row12)
        copy_fix_test d189f7a89a00acc8ae80ef4c11f476e67f6bee1a LiteDB.Tests/Engine/TransactionHandleCallbackLock_Tests.cs
        filter="FullyQualifiedName~TransactionHandleCallbackLock_Tests" ;;
    repro-row13)
        test=LiteDB.Tests/Engine/TransactionHandleRawCloseDependency_Tests.cs
        if [ ! -f "$test" ]; then
            # The first case needs Exclusive(closing:), which only the fix has; keep the second.
            git show 0d5e5effa302f76c970a775d14c767f8a3732d7d:$test | python3 -c '
import sys
s = sys.stdin.read()
a = s.index("        [Fact]\n        public async Task Close_wakes_fresh_work")
b = s.index("        [Fact]\n        public async Task Raw_close_rejects_fresh_callback_dependency")
sys.stdout.write(s[:a] + s[b:])' > "$test" || exit 2
        fi
        cp "$here/repro/WaitGraphRepro_Row13Driver_Tests.cs" LiteDB.Tests/Engine/
        filter="FullyQualifiedName~TransactionHandleRawCloseDependency_Tests|FullyQualifiedName~WaitGraphRepro_Row13" ;;
    *) echo "unknown mode $mode" >&2; exit 2 ;;
esac
filter=${WAITGRAPH_FILTER:-$filter}

dotnet build LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 -p:TestingEnabled=true > "$artifacts/build.log" 2>&1 \
    || { tail -20 "$artifacts/build.log" >&2; exit 2; }
report="$artifacts/waitgraph.jsonl"; rm -f "$report"
LITEDB_WAITGRAPH_REPORT="$report" LITEDB_WAITGRAPH_FAIL="$fail" \
    dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --no-build --filter "$filter" \
    --blame-hang-timeout 90s --blame-hang-dump-type none \
    --logger "trx;LogFileName=$artifacts/tests.trx" > "$artifacts/tests.log" 2>&1
echo "tests exit code $? (test failures do not fire the net)"

[ -f "$report" ] || { echo "no wait-for graph findings"; exit 0; }
python3 - "$report" <<'EOF'
import json, sys
fired = 0
for line in open(sys.argv[1]):
    finding = json.loads(line)
    context = finding["context"] or ""
    # The graph's own tests provoke findings on purpose and take them; skip their contexts.
    if "WaitGraph_Tests" in context or "WaitGraphSites_Tests" in context:
        continue
    print(("WAIT_FOR_CYCLE:" if finding["failing"] else "reported:") + finding["rule"],
          context, "atMs=%s" % finding["atMs"], sep="  ")
    fired |= finding["failing"]
sys.exit(1 if fired else 0)
EOF
