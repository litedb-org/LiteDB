#!/usr/bin/env python3
"""Verify released client callers and the documented pre-existing engine ABI limit."""
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
    parser.add_argument("--dev-dll", type=Path, required=True)
    parser.add_argument("--phase3-dll", type=Path, required=True)
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
            original = root / "original"
            runner.run("build-released-consumer", compat.build_command(project, args.candidate_dll.resolve(),
                       args.framework, original), contains=("Build succeeded",))
            runner.run("released-decorator", ["dotnet", str(original / "TransactionHandleValidation.dll"), "binary"],
                       contains=(compat.BINARY_PASS,))
            variants = {"released": original / "LiteDB.dll", "dev": args.dev_dll,
                        "phase3": args.phase3_dll, "phase4": args.candidate_dll}
            transcripts = {}
            manifest["libraries"] = {}
            for name, library in variants.items():
                folder = root / name
                shutil.copytree(original, folder)
                shutil.copy2(library, folder / "LiteDB.dll")
                manifest["libraries"][name] = compat.sha256(library)
                fixture = ["dotnet", str(folder / "TransactionHandleValidation.dll")]
                result = runner.run(name + "-callers", [*fixture, "callers"],
                                    contains=("PASS released client callers",))
                if compat.sha256(compat.loaded_library(result)) != compat.sha256(library):
                    raise RuntimeError(name + " library hash mismatch")
                transcripts[name] = [line for line in result.splitlines() if line.startswith("caller:")]
                if name != "released":
                    result = runner.run(name + "-documented-decorator-limitation", [*fixture, "binary"],
                                        expected="nonzero", contains=("System.TypeLoadException", "EnsureVectorIndex", "LegacyEngineDecorator"))
                    if compat.sha256(compat.loaded_library(result)) != compat.sha256(library):
                        raise RuntimeError(name + " limitation probe loaded the wrong DLL")
            if not transcripts["released"] or any(rows != transcripts["released"] for rows in transcripts.values()):
                raise RuntimeError("Released caller behavior differs across variants")
            manifest["caller_transcripts"] = transcripts
            manifest["documented_limitation"] = "v5.0.21 ILiteEngine implementers require recompilation since dev commit66ec615a1b03058a84e2841caf0a72b2a4a2ae1d"
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
