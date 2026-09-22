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
    private readonly Func<string> _captureDump;
    private readonly object _captureLock = new();
    private bool _captureAttempted;
    private string _captureReport;
    private int _retainFiles;

    internal SharedWorkerDiagnostics(string directory, bool retainOnTimeout, Func<string> captureDump = null)
    {
        _directory = directory;
        _retainOnTimeout = retainOnTimeout;
        _captureDump = captureDump;
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
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "timeout-" + Guid.NewGuid().ToString("N") + ".txt"), report);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report += $"\nDiagnostic capture failed: {ex.GetType().Name}: {ex.Message}";
        }
        // Every timeout caller must wait for the same capture before returning
        // to cancellation. Otherwise another worker could alter the stalled state
        // while the first caller is still starting the dump process.
        if (_captureDump != null)
        {
            lock (_captureLock)
            {
                if (!_captureAttempted)
                {
                    _captureAttempted = true;
                    try { _captureReport = _captureDump(); }
                    catch (Exception ex) { _captureReport = $"Dump capture failed: {ex.GetType().Name}: {ex.Message}"; }
                }
                report += "\n" + _captureReport;
            }
        }
        return report;
    }

    internal sealed class Worker
    {
        private readonly object _sync = new();
        private readonly Func<long> _elapsedMilliseconds;
        private string _stage = "queued";
        private int _completed;
        private int _thread;
        private long _lastProgress;
        private long _lastCompleted;

        internal Worker(Func<long> elapsedMilliseconds = null)
        {
            var elapsed = elapsedMilliseconds == null ? Stopwatch.StartNew() : null;
            _elapsedMilliseconds = elapsedMilliseconds ?? (() => elapsed.ElapsedMilliseconds);
            _lastCompleted = _elapsedMilliseconds();
        }

        internal long CompletedIdleMilliseconds
        {
            get { lock (_sync) return _elapsedMilliseconds() - _lastCompleted; }
        }

        internal void Progress(string stage, int? completed = null)
        {
            lock (_sync)
            {
                _stage = stage;
                if (completed.HasValue && completed.Value > _completed)
                {
                    _completed = completed.Value;
                    _lastCompleted = _elapsedMilliseconds();
                }
                _thread = Environment.CurrentManagedThreadId;
                _lastProgress = _elapsedMilliseconds();
            }
        }

        internal string Snapshot()
        {
            lock (_sync)
                return $"stage={_stage}; completed={_completed}; thread={_thread}; elapsedMs={_elapsedMilliseconds()}; lastProgressMs={_lastProgress}; idleMs={_elapsedMilliseconds() - _lastProgress}; lastCompletedMs={_lastCompleted}; completedIdleMs={_elapsedMilliseconds() - _lastCompleted}";
        }
    }
}
