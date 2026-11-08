#!/usr/bin/env python3
"""Run a genuine 5.0.21 consumer/decorator unchanged against the candidate."""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile

spec = importlib.util.spec_from_file_location("handle_compat", Path(__file__).with_name("test-transaction-handle-compatibility.py"))
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate-dll", type=Path, required=True)
    parser.add_argument("--framework", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    runner = compat.Runner(args.output)
    manifest = {"status": "fail", "package": "LiteDB", "version": "5.0.21",
                "candidate_sha256": compat.sha256(args.candidate_dll)}
    try:
        with tempfile.TemporaryDirectory(prefix="litedb-released-binary-") as temporary:
            root = Path(temporary)
            source = compat.copy_source(root, "consumer")
            # 5.0.21 has no vector types or member. This models its actual decorator surface.
            decorator = source / "LegacyEngineDecorator.cs"
            decorator.write_text("\n".join(line for line in decorator.read_text().splitlines()
                                           if "LiteDB.Vector" not in line and "EnsureVectorIndex" not in line) + "\n")
            project = source / "TransactionHandleValidation.csproj"
            import re
            project.write_text(re.sub(r'<Reference Include="LiteDB">.*?</Reference>',
                                      '<PackageReference Include="LiteDB" Version="5.0.21" />',
                                      project.read_text(), flags=re.S))
            original, swapped = root / "original", root / "swapped"
            runner.run("build-released-consumer", compat.build_command(project, args.candidate_dll.resolve(),
                       args.framework, original), contains=("Build succeeded",))
            shutil.copytree(original, swapped)
            shutil.copy2(args.candidate_dll, swapped / "LiteDB.dll")
            for name, folder in [("released", original), ("candidate", swapped)]:
                result = runner.run(name + "-binary", ["dotnet", str(folder / "TransactionHandleValidation.dll"), "binary"],
                                    contains=(compat.BINARY_PASS,))
                if name == "candidate" and compat.sha256(compat.loaded_library(result)) != manifest["candidate_sha256"]:
                    raise RuntimeError("Candidate library hash mismatch")
            manifest["status"] = "pass"
    except RuntimeError as error:
        manifest["error"] = str(error)
        print(error)
    finally:
        manifest["results"] = runner.results
        (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return 0 if manifest["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
