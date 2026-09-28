#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
image=${1:-litedb-admission-glibc231}
outputs="$repo_root/tools/NativeAdmissionValidation/bin/Release/net8.0"
results="$repo_root/LiteDB.Tests/TestResults"
mkdir -p "$results"
fixture=$(mktemp -d "$results/native-admission-bind.XXXXXX")
container_user="$(id -u):$(id -g)"
case "$(docker info --format '{{json .SecurityOptions}}')" in
    *rootless*) container_user=0:0 ;;
esac
case "$(uname -m)" in
    x86_64) architecture=x64 ;;
    aarch64) architecture=arm64 ;;
    *) printf 'Unsupported test architecture\n' >&2; exit 1 ;;
esac
# Both aliases expose the entire data/WAL directory inside one named-mutex
# namespace. No privileged container, nested VM, or filesystem mutation is needed.
docker run --rm --network none --user "$container_user" \
    -e LITEDB_EXPECTED_RUNTIME_MAJOR=8 -e LITEDB_EXPECTED_ARCHITECTURE="$architecture" \
    --mount "type=bind,src=$outputs,dst=/probe,readonly" \
    --mount "type=bind,src=$fixture,dst=/a" \
    --mount "type=bind,src=$fixture,dst=/b" \
    "$image" dotnet /probe/NativeAdmissionValidation.dll scenario /a /b | tee "$fixture/result.txt"
