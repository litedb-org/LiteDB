"""Compare measured dev drops with 5.0.9; runner success alone is not this regression check."""
import json
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
    old, new = measurements
    assert old["rows"] == new["rows"] and old["rows"] >= 100000
    ratio = new["medianMilliseconds"] / old["medianMilliseconds"]
    print(json.dumps({"package": old, "dev": new, "ratio": ratio}, indent=2))
    assert ratio <= 2, "dev drop is over twice as slow as 5.0.9 at the same size"
