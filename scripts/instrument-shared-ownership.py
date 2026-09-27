#!/usr/bin/env python3
"""Instrument disposable benchmark checkouts, never a shipping build or acceptance run."""
import argparse
import difflib
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('roots', nargs='+', type=Path)
parser.add_argument('--output', required=True, type=Path, help='Retain the exact diagnostic source patch')
args = parser.parse_args()
patch = []

def record(root, relative, before, after):
    path = root / relative
    patch.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True),
                                     fromfile=str(path), tofile=str(path)))
    path.write_text(after)

def wrap(text, signature, metric):
    # These fixed methods contain no braces in literals; reject missing/ambiguous anchors.
    if text.count(signature) != 1:
        raise ValueError('Unexpected source for ' + signature)
    opening = text.index('{', text.index(signature))
    depth, closing = 1, opening + 1
    while depth:
        depth += (text[closing] == '{') - (text[closing] == '}')
        closing += 1
    body = text[opening + 1:closing - 1]
    instrumented = ('\n            var profileStart = System.Diagnostics.Stopwatch.GetTimestamp();\n'
                    '            try\n            {' + body + '\n            }\n'
                    '            finally\n            {\n'
                    f'                SharedReaderProfile.Record{metric}(profileStart);\n'
                    '            }\n        ')
    return text[:opening + 1] + instrumented + text[closing - 1:]

for root in args.roots:
    relative = Path('LiteDB/Client/Shared/SharedEngine.Waiters.cs')
    before = (root / relative).read_text()
    assert before.count('this.AddMutexWaiter();') == before.count('this.RemoveMutexWaiter();') == 1
    after = before.replace('this.AddMutexWaiter();',
        'var profileStart = System.Diagnostics.Stopwatch.GetTimestamp();\n            this.AddMutexWaiter();')
    after = after.replace('this.RemoveMutexWaiter();',
        'this.RemoveMutexWaiter();\n                SharedReaderProfile.RecordWait(profileStart);')
    record(root, relative, before, after)
    for name, signature, metric in (
        ('SharedMutexOwner.cs', 'private bool Send(Command command)', 'Send'),
        ('SharedEngine.cs', 'private void CloseDatabase(SharedMutexPin use = null, bool hold = false, int generation = -1)', 'Close'),
        ('SharedEngine.cs', 'private LiteEngine CreateEngine(bool recoveredAbandonedOwner, EngineSettings settings = null)', 'Open'),
        ('SharedMutexTurnstile.cs', 'public void Wait(Mutex mutex)', 'Native'),
        ('SharedEngine.Query.cs', 'private IBsonDataReader QuerySnapshot(string collection, Query query, LiteEngine snapshot)', 'Query'),
        ('SharedEngine.CoordinatedReads.cs', 'private static void RetireSnapshot(CachedSharedSnapshot snapshot)', 'Retire')):
        relative = Path('LiteDB/Client/Shared') / name
        if not (root / relative).exists() and metric == 'Retire':
            continue
        before = (root / relative).read_text()
        record(root, relative, before, wrap(before, signature, metric))
    relative = Path('LiteDB/Engine/Disk/DiskService.WalWrite.cs')
    before = (root / relative).read_text()
    record(root, relative, before, wrap(before, 'public int WriteLogDisk(IEnumerable<PageBuffer> pages, Action<uint, long> written = null,', 'Wal'))
    relative = Path('LiteDB/Engine/Disk/DiskService.DurableFlush.cs')
    before = (root / relative).read_text()
    record(root, relative, before, wrap(before, 'private void FlushLogToDisk(Stream stream)', 'Flush'))
    helper = 'namespace LiteDB { internal static class SharedReaderProfile {\n'
    for metric in ('Wait', 'Open', 'Native', 'Query', 'Retire', 'Send', 'Close', 'Wal', 'Flush'):
        helper += (f'internal static long {metric}Ticks, {metric}Count;\n'
                   f'internal static void Record{metric}(long start) {{\n'
                   f'System.Threading.Interlocked.Add(ref {metric}Ticks, System.Diagnostics.Stopwatch.GetTimestamp() - start);\n'
                   f'System.Threading.Interlocked.Increment(ref {metric}Count); }}\n')
    helper += '} }\n'
    relative = Path('LiteDB/Client/Shared/SharedReaderProfile.cs')
    if (root / relative).exists():
        raise ValueError('Checkout is already instrumented')
    record(root, relative, '', helper)

args.output.write_text(''.join(patch))
print('DIAGNOSTIC ONLY: source was instrumented; these are not production acceptance timings.')
