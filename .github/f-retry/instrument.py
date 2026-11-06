"""Apply the same opt-in diagnostics to an exact phase2 or dev checkout."""
from pathlib import Path
import sys
root = Path(sys.argv[1])
p = root / 'LiteDB.Tests.SharedMutexHarness/MvccHarness.cs'
s = p.read_text(encoding='utf-8-sig')
old = 'for (var i = start; i < start + 20; i++) collection.Insert(new BsonDocument { ["_id"] = i });'
assert s.count(old) == 1
s = s.replace(old, '''Console.Error.WriteLine($"runtime {Environment.Version} arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} pid {Environment.ProcessId}");
                for (var i = start; i < start + 20; i++)
                {
                    Console.Error.WriteLine($"insert-begin {i} {DateTime.UtcNow:O}");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    collection.Insert(new BsonDocument { ["_id"] = i });
                    Console.Error.WriteLine($"insert-end {i} {DateTime.UtcNow:O} ms={clock.Elapsed.TotalMilliseconds:F3}");
                }''')
p.write_text(s)
p = root / 'LiteDB.Tests/Internals/MvccProcess.cs'
s = p.read_text(encoding='utf-8-sig')
old = '''var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout);'''
assert s.count(old) == 1
s = s.replace(old, '''string line;
            try { line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout); }
            catch (TimeoutException)
            {
                await RetryDiagnostics.Capture(_process.Id);
                throw;
            }''')
old = 'finally { _process.Dispose(); }'
assert s.count(old) == 1
s = s.replace(old, '''finally
            {
                RetryDiagnostics.SaveChild(_process.Id, _errors.GetAwaiter().GetResult());
                _process.Dispose();
            }''')
p.write_text(s)
(root / 'LiteDB.Tests/Internals/RetryDiagnostics.cs').write_text((Path(__file__).parent / 'RetryDiagnostics.cs').read_text())
