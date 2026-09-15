#!/usr/bin/env python3
"""Verify the consumer artifact contract reported in LiteDB issue #2320."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


CONTROL_VERSION = "5.0.21"
EXPECTED_SIGNER = "SignPath Foundation"


class HarnessError(RuntimeError):
    pass


def download_package(version: str, directory: pathlib.Path) -> pathlib.Path:
    normalized = version.lower()
    url = (
        "https://api.nuget.org/v3-flatcontainer/litedb/"
        f"{normalized}/litedb.{normalized}.nupkg"
    )
    destination = directory / f"LiteDB.{version}.nupkg"
    print(f"DOWNLOAD {url}")
    try:
        with urllib.request.urlopen(url, timeout=60) as response:
            destination.write_bytes(response.read())
    except Exception as exc:
        raise HarnessError(f"could not download {url}: {exc}") from exc
    return destination


def package_metadata(package: pathlib.Path) -> dict[str, str]:
    try:
        with zipfile.ZipFile(package) as archive:
            nuspecs = [name for name in archive.namelist() if "/" not in name and name.endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise HarnessError(f"expected one root nuspec, found {nuspecs}")
            root = ET.fromstring(archive.read(nuspecs[0]))
    except (OSError, zipfile.BadZipFile, ET.ParseError) as exc:
        raise HarnessError(f"invalid NuGet package {package}: {exc}") from exc

    def value(name: str) -> str:
        node = root.find(f".//{{*}}{name}")
        return "" if node is None or node.text is None else node.text.strip()

    repository = root.find(".//{*}repository")
    return {
        "id": value("id"),
        "version": value("version"),
        "repositoryUrl": "" if repository is None else repository.attrib.get("url", ""),
        "repositoryBranch": "" if repository is None else repository.attrib.get("branch", ""),
        "repositoryCommit": "" if repository is None else repository.attrib.get("commit", ""),
    }


def nuget_signatures(package: pathlib.Path) -> tuple[list[dict[str, str]], str]:
    command = ["dotnet", "nuget", "verify", "--all", str(package)]
    try:
        result = subprocess.run(command, text=True, capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise HarnessError(f"could not run {' '.join(command)}: {exc}") from exc
    output = (result.stdout + "\n" + result.stderr).strip()
    if result.returncode != 0 and "NU3004" in output and "not signed" in output.casefold():
        return [], output
    if result.returncode != 0:
        raise HarnessError(f"dotnet nuget verify exited {result.returncode}:\n{output}")

    signatures: list[dict[str, str]] = []
    current: dict[str, str] | None = None
    for line in output.splitlines():
        match = re.match(r"\s*Signature type:\s*(\S+)\s*$", line)
        if match:
            current = {"type": match.group(1), "subject": ""}
            signatures.append(current)
            continue
        match = re.match(r"\s*Subject Name:\s*(.+?)\s*$", line)
        if match and current is not None:
            current["subject"] = match.group(1)
    return signatures, output


def pe_certificate_table(data: bytes) -> tuple[int, int]:
    if len(data) < 64 or data[:2] != b"MZ":
        raise HarnessError("assembly is not a PE file")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if pe_offset + 24 > len(data) or data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise HarnessError("assembly has an invalid PE header")
    optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
    optional = pe_offset + 24
    if optional + optional_size > len(data):
        raise HarnessError("assembly has a truncated optional header")
    magic = struct.unpack_from("<H", data, optional)[0]
    if magic == 0x10B:
        directory_count_offset, directories_offset = optional + 92, optional + 96
    elif magic == 0x20B:
        directory_count_offset, directories_offset = optional + 108, optional + 112
    else:
        raise HarnessError(f"assembly has unsupported PE magic 0x{magic:04x}")
    directory_count = struct.unpack_from("<I", data, directory_count_offset)[0]
    if directory_count <= 4 or directories_offset + 40 > optional + optional_size:
        return 0, 0
    certificate_offset, certificate_size = struct.unpack_from("<II", data, directories_offset + 32)
    if certificate_offset == 0 and certificate_size == 0:
        return 0, 0
    if certificate_offset == 0 or certificate_size < 8 or certificate_offset + certificate_size > len(data):
        raise HarnessError("assembly has an invalid certificate-table range")
    length, revision, certificate_type = struct.unpack_from("<IHH", data, certificate_offset)
    if length < 8 or length > certificate_size or revision != 0x0200 or certificate_type != 0x0002:
        raise HarnessError("assembly has a malformed WIN_CERTIFICATE record")
    if data[certificate_offset + 8:certificate_offset + 9] != b"\x30":
        raise HarnessError("assembly certificate is not a DER PKCS#7 sequence")
    return certificate_offset, certificate_size


def verify_authenticode(path: pathlib.Path, expected_signer: str) -> str:
    if os.name == "nt":
        shell = shutil.which("pwsh") or shutil.which("powershell")
        if shell is None:
            raise HarnessError("PowerShell is required to validate Authenticode on Windows")
        script = (
            "$s=Get-AuthenticodeSignature -LiteralPath $args[0];"
            "[pscustomobject]@{Status=[string]$s.Status;Subject=$s.SignerCertificate.Subject}"
            "|ConvertTo-Json -Compress; if($s.Status -ne 'Valid'){exit 1}"
        )
        result = subprocess.run(
            [shell, "-NoProfile", "-NonInteractive", "-Command", script, str(path)],
            text=True,
            capture_output=True,
            timeout=60,
        )
    else:
        verifier = shutil.which("osslsigncode")
        if verifier is None:
            raise HarnessError(
                "a certificate table is present, but osslsigncode is unavailable for cryptographic verification"
            )
        result = subprocess.run(
            [verifier, "verify", "-in", str(path)], text=True, capture_output=True, timeout=60
        )
    output = (result.stdout + "\n" + result.stderr).strip()
    if result.returncode != 0:
        raise HarnessError(f"Authenticode validation failed for {path.name}:\n{output}")
    if expected_signer.casefold() not in output.casefold():
        raise HarnessError(
            f"valid Authenticode signature did not identify expected signer {expected_signer!r}:\n{output}"
        )
    return output


def check_author_signature(package: pathlib.Path, expected_signer: str) -> tuple[bool, list[dict[str, str]]]:
    signatures, _ = nuget_signatures(package)
    matching = [
        signature
        for signature in signatures
        if signature["type"].casefold() == "author"
        and expected_signer.casefold() in signature["subject"].casefold()
    ]
    return bool(matching), signatures


def check_target(
    package: pathlib.Path,
    expected_version: str,
    expected_commit: str | None,
    expected_signer: str,
) -> tuple[list[str], dict[str, object]]:
    metadata = package_metadata(package)
    if metadata["id"] != "LiteDB":
        raise HarnessError(f"expected package id LiteDB, got {metadata['id']!r}")
    if metadata["version"] != expected_version:
        raise HarnessError(f"expected package version {expected_version}, got {metadata['version']!r}")
    if expected_commit and metadata["repositoryCommit"].casefold() != expected_commit.casefold():
        raise HarnessError(
            f"expected repository commit {expected_commit}, got {metadata['repositoryCommit']!r}"
        )

    author_ok, signatures = check_author_signature(package, expected_signer)
    failures: list[str] = []
    if not author_ok:
        failures.append(f"NuGet package lacks an Author signature from {expected_signer}")

    assemblies: list[dict[str, object]] = []
    try:
        with zipfile.ZipFile(package) as archive, tempfile.TemporaryDirectory(prefix="litedb-2320-pe-") as raw:
            dll_names = sorted(
                name for name in archive.namelist()
                if name.casefold().endswith(".dll") and (name.startswith("lib/") or name.startswith("runtimes/"))
            )
            if not dll_names:
                raise HarnessError("package contains no lib/ or runtimes/ assemblies")
            extraction = pathlib.Path(raw)
            for index, name in enumerate(dll_names):
                data = archive.read(name)
                offset, size = pe_certificate_table(data)
                record: dict[str, object] = {"path": name, "certificateOffset": offset, "certificateSize": size}
                assemblies.append(record)
                if size == 0:
                    failures.append(f"{name} has no PE Authenticode certificate table")
                    continue
                path = extraction / f"{index}.dll"
                path.write_bytes(data)
                record["verifier"] = verify_authenticode(path, expected_signer).splitlines()[-1]
    except (OSError, zipfile.BadZipFile, KeyError) as exc:
        raise HarnessError(f"could not inspect package assemblies: {exc}") from exc

    report: dict[str, object] = {
        "package": str(package.resolve()),
        "sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
        "metadata": metadata,
        "signatures": signatures,
        "assemblies": assemblies,
    }
    return failures, report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--version", help="download this immutable LiteDB version from NuGet.org")
    source.add_argument("--package", type=pathlib.Path, help="inspect an existing nupkg")
    parser.add_argument("--expected-version", help="required with --package; guards against substituting an old signed package")
    parser.add_argument("--expected-commit", help="require this exact nuspec repository commit")
    parser.add_argument("--expected-signer", default=EXPECTED_SIGNER)
    parser.add_argument("--author-control-version", default=CONTROL_VERSION)
    parser.add_argument("--pe-structure-control", type=pathlib.Path)
    parser.add_argument("--stage", choices=("published", "local-pre-sign"), default="published")
    args = parser.parse_args()
    if args.package is not None and not args.expected_version:
        parser.error("--expected-version is required with --package")
    return args


def main() -> int:
    args = parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix="litedb-2320-download-") as raw:
            directory = pathlib.Path(raw)
            package = args.package or download_package(args.version, directory)
            expected_version = args.expected_version or args.version

            control = download_package(args.author_control_version, directory)
            control_ok, control_signatures = check_author_signature(control, args.expected_signer)
            if not control_ok:
                raise HarnessError(
                    f"positive control LiteDB {args.author_control_version} was not Author-signed by {args.expected_signer}: "
                    f"{control_signatures}"
                )
            print(f"CONTROL_2320_AUTHOR_OK version={args.author_control_version} signer={args.expected_signer}")

            if args.pe_structure_control:
                control_data = args.pe_structure_control.read_bytes()
                offset, size = pe_certificate_table(control_data)
                if size == 0:
                    raise HarnessError(f"PE structure control {args.pe_structure_control} is not Authenticode-signed")
                print(f"CONTROL_2320_PE_STRUCTURE_OK offset={offset} size={size} path={args.pe_structure_control}")

            failures, report = check_target(
                package, expected_version, args.expected_commit, args.expected_signer
            )
            print(json.dumps(report, indent=2, sort_keys=True))
            if args.stage == "local-pre-sign":
                print("PRE_SIGN_2320_ONLY: local pack output is not evidence about the published signing stage")
                for failure in failures:
                    print(f"PRE_SIGN_OBSERVATION: {failure}")
                return 3
            if failures:
                for failure in failures:
                    print(f"BUG_2320_CONFIRMED: {failure}")
                return 1
            print("VERIFIED_2320_SIGNING: package author signature and every embedded assembly passed independent checks")
            return 0
    except HarnessError as exc:
        print(f"HARNESS_2320_ERROR: {exc}", file=sys.stderr)
        return 2
    except Exception as exc:
        print(f"HARNESS_2320_ERROR: unexpected {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
