#!/usr/bin/env python3
"""Verify the published collection-name rule, with executable positive controls."""
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


def main():
    root = Path(__file__).resolve().parents[2]
    runtime = subprocess.run([
        "dotnet", "test", str(root / "LiteDB.Tests"), "-c", "Release", "-f", "net8.0",
        "-p:TestingEnabled=true", "--filter",
        "FullyQualifiedName~Issue2746_Tests.Collection_name_contract_distinguishes_initial_digits_from_later_digits",
    ], capture_output=True, text=True, timeout=240)
    print(runtime.stdout)
    if runtime.returncode != 0 or not re.search(r"Passed:\s+3.*Total:\s+3", runtime.stdout):
        raise RuntimeError("all three valid/invalid-name and recovery controls must execute and pass")
    url = "https://www.litedb.org/docs/collections/"
    with urllib.request.urlopen(url, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f"unexpected HTTP {response.status}")
        body = response.read()
    parser = Text()
    parser.feed(body.decode("utf-8"))
    text = " ".join(" ".join(parser.parts).split())
    start = text.find("Each collection must have a unique name")
    end = text.find("Collections are auto created", start)
    if start < 0 or end < 0:
        raise RuntimeError("published naming section changed; review its semantics before accepting a new matcher")
    rules = text[start:end]
    patterns = [
        r"(?:cannot|can't|must not|may not|not allowed to|not)\s+(?:start|begin)[^.]{0,60}(?:digit|number)",
        r"(?:start|begin)[^.]{0,60}(?:letter|alphabetic)",
        r"(?:first|initial)\s+character[^.]{0,80}(?:letter|alphabetic|not[^.]{0,30}(?:digit|number))",
    ]
    explanation = any(re.search(pattern, rules, re.I) for pattern in patterns)
    print(json.dumps({"url": url, "sha256": hashlib.sha256(body).hexdigest(),
                      "visibleNamingRules": rules, "explainsInitialDigitRestriction": explanation}, indent=2))
    if not explanation:
        print("BUG_2746_CONFIRMED: published naming rule permits digits without explaining the initial-digit restriction")
        return 1
    print("VERIFIED_2746: naming explanation and all independent runtime examples agree")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("HARNESS_2746_ERROR:", error, file=sys.stderr)
        sys.exit(2)
