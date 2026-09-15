#!/usr/bin/env python3
"""Audit v4 publication and advisory metadata; does not execute deserialization."""
import hashlib
import io
import json
import re
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

REPOSITORY = 'litedb-org/LiteDB'
BACKPORT = '3af2be2b91b365ea04b2f5e5255d2ba91ebb14c6'


def gh(*arguments):
    result = subprocess.run(['gh', *arguments], capture_output=True, text=True, check=True, timeout=60)
    return json.loads(result.stdout)


def numeric(version):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('unsupported stable version: ' + version)
    return tuple(map(int, version.split('.')))


def affected(version, ranges):
    value = numeric(version)
    for rule in ranges:
        matches = []
        for constraint in rule.split(','):
            match = re.fullmatch(r'\s*(<=|>=|<|>|=)?\s*(\d+\.\d+\.\d+)\s*', constraint)
            if not match:
                raise ValueError('advisory range needs semantic review: ' + rule)
            operator, bound = match.group(1) or '=', numeric(match.group(2))
            matches.append({'<': value < bound, '<=': value <= bound, '>': value > bound,
                            '>=': value >= bound, '=': value == bound}[operator])
        if all(matches):
            return True
    return False


def package(version):
    url = f'https://api.nuget.org/v3-flatcontainer/litedb/{version}/litedb.{version}.nupkg'
    with urllib.request.urlopen(url, timeout=40) as response:
        body = response.read()
    with zipfile.ZipFile(io.BytesIO(body)) as archive:
        nuspecs = [name for name in archive.namelist() if '/' not in name and name.endswith('.nuspec')]
        if len(nuspecs) != 1:
            raise ValueError('package must contain exactly one root nuspec')
        root = ET.fromstring(archive.read(nuspecs[0]))
        if root.findtext('.//{*}id') != 'LiteDB' or root.findtext('.//{*}version') != version:
            raise ValueError('downloaded package identity mismatch')
        assemblies = [name for name in archive.namelist() if name.startswith('lib/') and name.endswith('/LiteDB.dll')]
        if not assemblies:
            raise ValueError('published package has no library assemblies')
    return {'version': version, 'url': url, 'sha256': hashlib.sha256(body).hexdigest(), 'assemblies': assemblies}


def main():
    with urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/litedb/index.json', timeout=30) as response:
        versions = json.load(response)['versions']
    v4 = [version for version in versions if re.fullmatch(r'4\.\d+\.\d+', version)]
    if '4.1.4' not in v4 or '5.0.21' not in versions:
        raise ValueError('known published package controls missing from version index')
    latest = max(v4, key=numeric)
    advisory = gh('api', '/advisories/GHSA-3x49-g6rc-c284')
    if advisory['cve_id'] != 'CVE-2022-23535':
        raise ValueError('wrong advisory identity')
    ranges = [item['vulnerable_version_range'] for item in advisory['vulnerabilities']
              if item['package'] == {'ecosystem': 'nuget', 'name': 'LiteDB'}]
    if not ranges or not affected('4.1.4', ranges) or affected('5.0.21', ranges):
        raise ValueError('independent affected/unaffected advisory controls failed')
    target = package(latest)
    control = package('5.0.21')
    release = gh('release', 'view', 'v' + latest, '-R', REPOSITORY,
                 '--json', 'tagName,isDraft,isPrerelease,publishedAt,url')
    comparison = gh('api', f'repos/{REPOSITORY}/compare/{BACKPORT}...v{latest}')
    contains_backport = comparison['status'] in ('ahead', 'identical')
    problems = []
    if not contains_backport:
        problems.append('latest published stable v4 tag does not contain the merged backport')
    if affected(latest, ranges):
        problems.append('public advisory still classifies the latest published v4 version as affected')
    if release['isDraft'] or release['isPrerelease']:
        problems.append('matching GitHub release is not a public stable release')
    print(json.dumps({'target': target, 'unaffectedControl': control, 'release': release,
                      'advisoryUpdatedAt': advisory['updated_at'], 'advisoryRanges': ranges,
                      'tagContainsMergedBackport': contains_backport, 'problems': problems}, indent=2))
    if problems:
        print('BUG_2808_PUBLICATION_CONFIRMED: ' + '; '.join(problems))
        return 1
    print('VERIFIED_2808_PUBLICATION_METADATA: publication, ancestry and advisory agree; no runtime security verdict')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('HARNESS_2808_ERROR:', error, file=sys.stderr)
        sys.exit(2)
