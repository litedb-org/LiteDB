#!/usr/bin/env python3
"""Check the public FileStorage size claim against the BSON page and runtime."""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import subprocess
import sys
import urllib.request


class Text(HTMLParser):
    def __init__(self):
        super().__init__()
        self.parts = []
        self.hidden = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style"):
            self.hidden += 1

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.hidden -= 1

    def handle_data(self, data):
        if not self.hidden:
            self.parts.append(data)


def fetch(page):
    url = "https://www.litedb.org/docs/" + page + "/"
    with urllib.request.urlopen(url, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f"unexpected HTTP {response.status} for {url}")
        body = response.read()
    parser = Text()
    parser.feed(body.decode("utf-8"))
    return " ".join(" ".join(parser.parts).split()), {
        "url": url, "sha256": hashlib.sha256(body).hexdigest()
    }


def main():
    root = Path(__file__).resolve().parents[2]
    runtime = subprocess.run([
        "dotnet", "test", str(root / "LiteDB.Tests"), "-c", "Release", "-f", "net8.0",
        "-p:TestingEnabled=true", "--filter", "FullyQualifiedName~Issue2796_Tests",
    ], capture_output=True, text=True, timeout=240)
    print(runtime.stdout)
    if runtime.returncode != 0 or not re.search(r"Passed:\s+1.*Total:\s+1", runtime.stdout):
        raise RuntimeError("the runtime boundary/embedded-document/DbRef controls did not all execute and pass")
    storage, storage_identity = fetch("filestorage")
    bson, bson_identity = fetch("bsondocument")
    if "FileStorage" not in storage or "_files" not in storage or "_chunks" not in storage:
        raise RuntimeError("FileStorage page identity/content preconditions failed")
    if "BsonDocument" not in bson or not re.search(r"16\s*MB", bson, re.I):
        raise RuntimeError("independent BSON-document size documentation is missing or changed")
    wrong_claims = re.findall(r"[^.]*\b1\s*MB\b[^.]*[.]?", storage, re.I)
    print(json.dumps({"fileStorage": storage_identity, "bsonDocument": bson_identity,
                      "obsoleteOneMegabyteClaims": wrong_claims}, indent=2))
    if wrong_claims:
        print("BUG_2796_CONFIRMED: FileStorage still claims 1MB despite the verified 16MB runtime boundary")
        return 1
    print("VERIFIED_2796: obsolete claim absent; independent docs and all runtime controls passed")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("HARNESS_2796_ERROR:", error, file=sys.stderr)
        sys.exit(2)
