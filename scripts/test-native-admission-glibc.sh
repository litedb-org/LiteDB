#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
case "$(uname -m)" in
    x86_64) architecture=x64 ;;
    aarch64) architecture=arm64 ;;
    *) printf 'Unsupported test architecture\n' >&2; exit 1 ;;
esac
image=litedb-admission-glibc231
docker build -t "$image" -f "$repo_root/.github/docker/native-admission-glibc231.Dockerfile" "$repo_root/.github/docker"
test "$(docker run --rm "$image" getconf GNU_LIBC_VERSION)" = 'glibc 2.31'
docker run --rm "$image" dotnet --info

results="$repo_root/LiteDB.Tests/TestResults"
mkdir -p "$results"
container_user="$(id -u):$(id -g)"
case "$(docker info --format '{{json .SecurityOptions}}')" in
    *rootless*) container_user=0:0 ;; # The invoking host user maps to container uid 0.
esac
# Use packaged outputs and the invoking host user's permissions; no network or
# restore is needed. Parent and children assert actual runtime/architecture.
docker run --rm --network none --user "$container_user" \
    -e LITEDB_EXPECTED_RUNTIME_MAJOR=8 -e LITEDB_EXPECTED_ARCHITECTURE="$architecture" \
    -e LITEDB_MAPPED_TEST_DIRECTORY=/results \
    --mount "type=bind,src=$repo_root,dst=/repo,readonly" \
    --mount "type=bind,src=$results,dst=/results" \
    --workdir /repo/LiteDB.Tests/bin/Release/net8.0 \
    "$image" dotnet vstest LiteDB.Tests.dll \
    /Settings:/repo/tests.runsettings /ResultsDirectory:/results \
    "/Logger:trx;LogFileName=NativeAdmission-glibc231-$architecture.trx" \
    '/TestCaseFilter:FullyQualifiedName~NativeAdmission|FullyQualifiedName~SharedMode|FullyQualifiedName~SharedAdmissionLifetime|FullyQualifiedName~DirectModeAdmission|FullyQualifiedName~Rebuild|FullyQualifiedName~TestHost_Tests'

python3 - "$results/NativeAdmission-glibc231-$architecture.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = ET.parse(sys.argv[1]).findall('.//t:UnitTestResult', ns)
for guard in ('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks'):
    assert sum(r.get('testName', '').endswith(guard) and r.get('outcome') == 'Passed' for r in results) == 1, guard
assert any('NativeAdmissionProcess_Tests' in r.get('testName', '') and r.get('outcome') == 'Passed' for r in results)
PY

bash "$repo_root/scripts/test-native-admission-bind-mount.sh" "$image"
