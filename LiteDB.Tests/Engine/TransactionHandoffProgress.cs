using System;
using System.Linq;

namespace LiteDB.Tests.Engine
{
    /// <summary>Only a successful handoff renews that worker's progress deadline.</summary>
    internal sealed class TransactionHandoffProgress
    {
        private readonly object _gate = new object();
        private readonly int[] _completed, _rejected;
        private readonly long[] _lastSuccess;
        private readonly string[] _stage;
        private readonly int _target;

        internal TransactionHandoffProgress(int workers, int target, long started = 0)
        {
            _completed = new int[workers];
            _rejected = new int[workers];
            _lastSuccess = Enumerable.Repeat(started, workers).ToArray();
            _stage = Enumerable.Repeat("not scheduled", workers).ToArray();
            _target = target;
        }

        internal void Attempt(int worker) { lock (_gate) _stage[worker] = "inside Run"; }
        internal void Rejected(int worker)
        {
            lock (_gate) { _rejected[worker]++; _stage[worker] = "overlap refused"; }
        }
        internal void Succeeded(int worker, long elapsed)
        {
            lock (_gate)
            {
                _completed[worker]++;
                _lastSuccess[worker] = elapsed;
                _stage[worker] = "returned successfully";
            }
        }
        internal bool AllCompleted { get { lock (_gate) return _completed.All(count => count == _target); } }

        internal string Timeout(long elapsed)
        {
            lock (_gate)
            {
                if (elapsed >= 60000) return "Handoff total limit (60s). " + Describe(elapsed);
                for (var worker = 0; worker < _completed.Length; worker++)
                    if (_completed[worker] < _target && elapsed - _lastSuccess[worker] >= 15000)
                        return $"Worker {worker} made no successful handoff for 15s. " + Describe(elapsed);
                return null;
            }
        }

        internal string Describe(long elapsed)
        {
            lock (_gate)
                return $"elapsedMs={elapsed}; " + string.Join("; ", Enumerable.Range(0, _completed.Length).Select(worker =>
                    $"worker={worker}, completed={_completed[worker]}/{_target}, rejected={_rejected[worker]}, " +
                    $"lastSuccessMs={_lastSuccess[worker]}, stage={_stage[worker]}"));
        }
    }
}
