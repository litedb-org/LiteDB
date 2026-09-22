using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Engine;

internal sealed class SharedWorkerDiagnostics
{
    private readonly ConcurrentDictionary<string, Worker> _workers = new();
    private readonly string _directory;
    private readonly bool _retainOnTimeout;
    private int _retainFiles;

    internal SharedWorkerDiagnostics(string directory, bool retainOnTimeout)
    {
        _directory = directory;
        _retainOnTimeout = retainOnTimeout;
    }

    internal bool RetainFiles => Volatile.Read(ref _retainFiles) != 0;
    internal Worker Track(string name) => _workers.GetOrAdd(name, _ => new Worker());

    internal string Timeout(string name)
    {
        if (_retainOnTimeout) Interlocked.Exchange(ref _retainFiles, 1);
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        var report = $"Timeout: {name}; observer thread={Environment.CurrentManagedThreadId}\n" +
            $"ThreadPool available/max: workers={availableWorkers}/{maxWorkers}, io={availableIo}/{maxIo}\n" +
            "Retained files are original files after test-host exit, not an atomic snapshot.\n" +
            string.Join("\n", _workers.OrderBy(x => x.Key).Select(x => x.Key + ": " + x.Value.Snapshot()));
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "timeout-" + Guid.NewGuid().ToString("N") + ".txt"), report);
        return report;
    }

    internal sealed class Worker
    {
        private readonly object _sync = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private string _stage = "queued";
        private int _completed;
        private int _thread;
        private long _lastProgress;

        internal void Progress(string stage, int? completed = null)
        {
            lock (_sync)
            {
                _stage = stage;
                if (completed.HasValue) _completed = completed.Value;
                _thread = Environment.CurrentManagedThreadId;
                _lastProgress = _elapsed.ElapsedMilliseconds;
            }
        }

        internal string Snapshot()
        {
            lock (_sync)
                return $"stage={_stage}; completed={_completed}; thread={_thread}; elapsedMs={_elapsed.ElapsedMilliseconds}; lastProgressMs={_lastProgress}; idleMs={_elapsed.ElapsedMilliseconds - _lastProgress}";
        }
    }
}
