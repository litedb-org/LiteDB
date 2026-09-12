#!/usr/bin/env bash
set -euo pipefail

readonly ROOT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
readonly DOTNET_COMMAND="${DOTNET_COMMAND:-dotnet}"
readonly MODEL_COUNT=128
readonly PROPERTY_COUNT=8
readonly CHANGED_MODEL_INDEX=64
readonly BASE_FIELD_NAME="measurement_name"
readonly UPDATED_FIELD_NAME="measurement_name_updated"

SAMPLE_COUNT=5
KEEP_WORKSPACE=false

usage() {
    cat <<'EOF'
Usage: ./scripts/measure-source-generator-incremental-build.sh [--samples <count>] [--keep-workspace]

Measures controlled end-to-end Release build durations for a disposable net8.0
consumer with 128 [BsonSourceGenerated] models. Each sample includes:
  1. a clean build of the baseline synthetic consumer; and
  2. a non-clean build after changing one effective BSON field name in model 64.

The script is a local evidence-gathering tool. Its timings are not CI thresholds
and are valid only for the recorded machine, SDK, and invocation settings.
EOF
}

while (($# > 0)); do
    case "$1" in
        --samples)
            (($# >= 2)) || { echo "Missing value for --samples." >&2; exit 2; }
            SAMPLE_COUNT="$2"
            shift 2
            ;;
        --keep-workspace)
            KEEP_WORKSPACE=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

case "$SAMPLE_COUNT" in
    ''|*[!0-9]*|0)
        echo "--samples must be a positive integer." >&2
        exit 2
        ;;
esac

readonly WORKSPACE_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/litedb-source-generator-incremental.XXXXXX")"
readonly PROJECT_DIRECTORY="$WORKSPACE_DIRECTORY/SyntheticConsumer"
readonly MODELS_DIRECTORY="$PROJECT_DIRECTORY/Models"
readonly PROJECT_FILE="$PROJECT_DIRECTORY/SyntheticConsumer.csproj"
readonly GENERATED_DIRECTORY="$PROJECT_DIRECTORY/obj/generated"

cleanup() {
    if [[ "$KEEP_WORKSPACE" == true ]]; then
        printf '[MEASUREMENT] Preserved workspace: %s\n' "$WORKSPACE_DIRECTORY"
    else
        rm -rf "$WORKSPACE_DIRECTORY"
    fi
}
trap cleanup EXIT

write_project_file() {
    cat > "$PROJECT_FILE" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>\$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$ROOT_DIRECTORY/LiteDB/LiteDB.csproj" />
    <ProjectReference Include="$ROOT_DIRECTORY/LiteDB.SourceGenerator/LiteDB.SourceGenerator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
EOF
}

write_registration_source() {
    cat > "$PROJECT_DIRECTORY/Registration.cs" <<'EOF'
using LiteDB;
using LiteDB.Generated;

namespace SyntheticConsumer;

public static class SyntheticRegistration
{
    public static void Register(BsonMapper mapper)
    {
        LiteDbGeneratedMappings.Register(mapper);
    }
}
EOF
}

write_model_source() {
    local index="$1"
    local field_name="$2"
    local padded_index
    printf -v padded_index '%03d' "$index"

    cat > "$MODELS_DIRECTORY/MeasurementModel${padded_index}.cs" <<EOF
using System;
using System.Collections.Generic;
using LiteDB;

namespace SyntheticConsumer;

[BsonSourceGenerated]
public sealed class MeasurementModel${padded_index}
{
    public int Id { get; set; }

    [BsonField("$field_name")]
    public string Name { get; set; } = string.Empty;

    public int? RetryCount { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public List<string> Tags { get; set; } = [];

    public string[] StreamNames { get; set; } = [];

    public Dictionary<string, object?> Fields { get; set; } = [];
}
EOF
}

write_all_baseline_models() {
    local index
    for ((index = 1; index <= MODEL_COUNT; index++)); do
        write_model_source "$index" "$BASE_FIELD_NAME"
    done
}

prepare_clean_build() {
    rm -rf "$PROJECT_DIRECTORY/bin" "$PROJECT_DIRECTORY/obj"
    "$DOTNET_COMMAND" restore "$PROJECT_FILE" --nologo -p:GitVersionEnabled=false > "$WORKSPACE_DIRECTORY/prepare-restore.log"
}

run_timed_build() {
    local label="$1"
    local log_file="$WORKSPACE_DIRECTORY/${label}.log"
    local start_nanoseconds
    local end_nanoseconds

    start_nanoseconds="$(date +%s%N)"
    if ! "$DOTNET_COMMAND" build "$PROJECT_FILE" \
        --configuration Release \
        --no-restore \
        --nologo \
        -p:GitVersionEnabled=false \
        -p:UseSharedCompilation=false \
        > "$log_file" 2>&1; then
        cat "$log_file" >&2
        return 1
    fi
    end_nanoseconds="$(date +%s%N)"

    awk -v start="$start_nanoseconds" -v end="$end_nanoseconds" 'BEGIN { printf "%.3f", (end - start) / 1000000000 }'
}

verify_generated_mapping() {
    local generated_mapping_file
    generated_mapping_file="$(find "$GENERATED_DIRECTORY" -name LiteDbGeneratedMappings.g.cs -print -quit)"
    test -n "$generated_mapping_file"
    test "$(find "$GENERATED_DIRECTORY" -name LiteDbGeneratedMappings.g.cs -print | wc -l | tr -d '[:space:]')" = 1
    grep -q 'MeasurementModel128' "$generated_mapping_file"
    grep -q "FieldName = \"$UPDATED_FIELD_NAME\"" "$generated_mapping_file"
}

summarize_samples() {
    local label="$1"
    shift
    local -a samples=("$@")
    local mean
    local median

    mean="$(printf '%s\n' "${samples[@]}" | awk '{ sum += $1; count += 1 } END { printf "%.3f", sum / count }')"
    median="$(printf '%s\n' "${samples[@]}" | sort -n | awk '{ values[NR] = $1 } END { if (NR % 2 == 1) { printf "%.3f", values[(NR + 1) / 2] } else { printf "%.3f", (values[NR / 2] + values[(NR / 2) + 1]) / 2 } }')"

    printf '[MEASUREMENT] %s raw seconds: %s\n' "$label" "${samples[*]}"
    printf '[MEASUREMENT] %s mean seconds: %s; median seconds: %s\n' "$label" "$mean" "$median"
}

mkdir -p "$PROJECT_DIRECTORY" "$MODELS_DIRECTORY"
write_project_file
write_registration_source
write_all_baseline_models

if ! command -v "$DOTNET_COMMAND" >/dev/null 2>&1; then
    printf 'The configured DOTNET_COMMAND is not executable: %s\n' "$DOTNET_COMMAND" >&2
    exit 127
fi

printf 'LiteDB source-generator incremental build measurement\n'
printf '[MEASUREMENT] SDK: %s\n' "$("$DOTNET_COMMAND" --version)"
printf '[MEASUREMENT] OS: %s\n' "$(uname -srmo)"
printf '[MEASUREMENT] Logical processors: %s\n' "$(getconf _NPROCESSORS_ONLN)"
printf '[MEASUREMENT] Models: %d; persisted supported properties/model: %d; changed model: MeasurementModel%03d.cs\n' "$MODEL_COUNT" "$PROPERTY_COUNT" "$CHANGED_MODEL_INDEX"
printf '[MEASUREMENT] Command: dotnet build --configuration Release --no-restore --nologo -p:GitVersionEnabled=false -p:UseSharedCompilation=false\n'
printf '[MEASUREMENT] Restoring the disposable consumer outside timed regions.\n'
"$DOTNET_COMMAND" restore "$PROJECT_FILE" --nologo -p:GitVersionEnabled=false > "$WORKSPACE_DIRECTORY/restore.log"

printf '[MEASUREMENT] Running one untimed baseline warmup.\n'
prepare_clean_build
write_model_source "$CHANGED_MODEL_INDEX" "$BASE_FIELD_NAME"
run_timed_build warmup > /dev/null

cold_samples=()
update_samples=()
for ((sample = 1; sample <= SAMPLE_COUNT; sample++)); do
    printf '[MEASUREMENT] Sample %d/%d clean baseline build.\n' "$sample" "$SAMPLE_COUNT"
    prepare_clean_build
    write_model_source "$CHANGED_MODEL_INDEX" "$BASE_FIELD_NAME"
    cold_seconds="$(run_timed_build "sample-${sample}-clean")"
    cold_samples+=("$cold_seconds")

    printf '[MEASUREMENT] Sample %d/%d one-model update build.\n' "$sample" "$SAMPLE_COUNT"
    write_model_source "$CHANGED_MODEL_INDEX" "$UPDATED_FIELD_NAME"
    update_seconds="$(run_timed_build "sample-${sample}-update")"
    update_samples+=("$update_seconds")
    verify_generated_mapping

done

summarize_samples 'Clean baseline build' "${cold_samples[@]}"
summarize_samples 'One-model update build' "${update_samples[@]}"
printf '[MEASUREMENT] Passed: one generated registrar was emitted and reflected the deterministic model-64 field-name mutation.\n'
