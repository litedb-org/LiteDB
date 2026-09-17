"""Compare measured dev drops with 5.0.9; runner success alone is not this regression check."""
import json
import math
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="litedb-2846-report-") as directory:
    report = pathlib.Path(directory) / "report.json"
    subprocess.run(["dotnet", "run", "--project", "LiteDB.ReproRunner/LiteDB.ReproRunner.Cli", "-c", "Release",
                    "--", "run", "Issue_2846_DropPerformance", "--report", str(report)], cwd=root, check=True)
    result = json.loads(report.read_text())["Repros"][0]
    measurements = []
    for variant in ("Package", "Latest"):
        outcome = result[variant]
        assert outcome["Met"] and outcome["ExitCode"] == 10, outcome
        metrics = [json.loads(line["Text"].split("MEASURED_2846 ", 1)[1])
                   for line in outcome["Output"] if line["Text"].startswith("MEASURED_2846 ")]
        assert len(metrics) == 1, "missing or duplicate measurement"
        measurements.append(metrics[0])
    for metric in measurements:
        assert math.isfinite(metric["medianMilliseconds"]) and metric["medianMilliseconds"] > 0
        assert math.isfinite(metric["medianCompletedMilliseconds"])
        assert metric["medianCompletedMilliseconds"] >= metric["medianMilliseconds"]
    old, new = measurements
    assert old["rows"] == new["rows"] and old["rows"] >= 100000
    return_ratio = new["medianMilliseconds"] / old["medianMilliseconds"]
    ratio = new["medianCompletedMilliseconds"] / old["medianCompletedMilliseconds"]
    print(json.dumps({"package": old, "dev": new, "returnRatio": return_ratio, "completedRatio": ratio}, indent=2))
    assert ratio <= 2, "dev completed drop is over twice as slow as 5.0.9 at the same size"
