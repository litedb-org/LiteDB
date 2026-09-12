import json
import os
import sys
from pathlib import Path


def as_iterable(value):
    if value is None:
        return []
    if isinstance(value, (list, tuple, set)):
        return list(value)
    return [value]


def main():
    workspace = Path(os.getenv("GITHUB_WORKSPACE", Path.cwd()))
    os_matrix_path = workspace / ".github" / "os-matrix.json"
    repros_path = workspace / "repros.json"

    os_matrix = json.loads(os_matrix_path.read_text(encoding="utf-8"))
    platform_labels: dict[str, list[str]] = {}
    label_platform: dict[str, str] = {}

    for platform, labels in os_matrix.items():
        normalized = platform.lower()
        platform_labels[normalized] = []
        for label in labels:
            platform_labels[normalized].append(label)
            label_platform[label] = normalized

    all_labels = set(label_platform.keys())

    payload = json.loads(repros_path.read_text(encoding="utf-8"))
    repros = payload.get("repros") or []

    matrix_entries: list[dict[str, str]] = []
    skipped: list[str] = []
    unknown_platforms: set[str] = set()
    unknown_labels: set[str] = set()

    def collect_labels(items, local_unknown):
        labels = set()
        for item in items:
            key = item.lower()
            if key in platform_labels:
                labels.update(platform_labels[key])
            else:
                local_unknown.add(key)
                unknown_platforms.add(key)
        return labels

    for repro in repros:
        name = repro.get("name") or repro.get("id")
        if not name:
            continue

        supports = as_iterable(repro.get("supports"))
        normalized_supports = {str(item).lower() for item in supports if isinstance(item, str)}

        local_unknown_platforms: set[str] = set()
        local_unknown_labels: set[str] = set()

        if not normalized_supports or "any" in normalized_supports:
            candidate_labels = set(all_labels)
        else:
            candidate_labels = collect_labels(normalized_supports, local_unknown_platforms)

        os_constraints = repro.get("os") or {}
        include_platforms = {
            str(item).lower()
            for item in as_iterable(os_constraints.get("includePlatforms"))
            if isinstance(item, str)
        }
        include_labels = {
            str(item)
            for item in as_iterable(os_constraints.get("includeLabels"))
            if isinstance(item, str)
        }
        exclude_platforms = {
            str(item).lower()
            for item in as_iterable(os_constraints.get("excludePlatforms"))
            if isinstance(item, str)
        }
        exclude_labels = {
            str(item)
            for item in as_iterable(os_constraints.get("excludeLabels"))
            if isinstance(item, str)
        }

        if include_platforms:
            candidate_labels &= collect_labels(include_platforms, local_unknown_platforms)

        if include_labels:
            recognized_includes = {label for label in include_labels if label in label_platform}
            local_unknown_labels.update({label for label in include_labels if label not in label_platform})
            unknown_labels.update(local_unknown_labels)
            candidate_labels &= recognized_includes if recognized_includes else set()

        if exclude_platforms:
            candidate_labels -= collect_labels(exclude_platforms, local_unknown_platforms)

        if exclude_labels:
            recognized_excludes = {label for label in exclude_labels if label in label_platform}
            candidate_labels -= recognized_excludes
            unrecognized = {label for label in exclude_labels if label not in label_platform}
            local_unknown_labels.update(unrecognized)
            unknown_labels.update(unrecognized)

        candidate_labels &= all_labels

        if candidate_labels:
            for label in sorted(candidate_labels):
                matrix_entries.append(
                    {
                        "os": label,
                        "repro": name,
                        "platform": label_platform[label],
                    }
                )
        else:
            reason_segments = []
            if normalized_supports:
                reason_segments.append(f"supports={sorted(normalized_supports)}")
            if os_constraints:
                reason_segments.append("os constraints applied")
            if local_unknown_platforms:
                reason_segments.append(f"unknown platforms={sorted(local_unknown_platforms)}")
            if local_unknown_labels:
                reason_segments.append(f"unknown labels={sorted(local_unknown_labels)}")
            reason = "; ".join(reason_segments) or "no matching runners"
            skipped.append(f"{name} ({reason})")

    matrix_entries.sort(key=lambda entry: (entry["repro"], entry["os"]))

    summary_lines = []
    summary_lines.append(f"Total repro jobs: {len(matrix_entries)}")
    if skipped:
        summary_lines.append("")
        summary_lines.append("Skipped repros:")
        for item in skipped:
            summary_lines.append(f"- {item}")
    if unknown_platforms:
        summary_lines.append("")
        summary_lines.append("Unknown platforms encountered:")
        for item in sorted(unknown_platforms):
            summary_lines.append(f"- {item}")
    if unknown_labels:
        summary_lines.append("")
        summary_lines.append("Unknown labels encountered:")
        for item in sorted(unknown_labels):
            summary_lines.append(f"- {item}")

    summary_path = os.getenv("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write("\n".join(summary_lines) + "\n")

    outputs_path = os.getenv("GITHUB_OUTPUT")
    if not outputs_path:
        raise RuntimeError("GITHUB_OUTPUT is not defined.")

    with open(outputs_path, "a", encoding="utf-8") as handle:
        handle.write("matrix=" + json.dumps({"include": matrix_entries}) + "\n")
        handle.write("count=" + str(len(matrix_entries)) + "\n")
        handle.write("skipped=" + json.dumps(skipped) + "\n")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # pragma: no cover - diagnostic output for CI
        print(f"Failed to compose repro matrix: {exc}", file=sys.stderr)
        raise
