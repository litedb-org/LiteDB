using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Records a history for <see cref="PermittedHistoryChecker"/>: every call gets a clock tick just
    /// before invocation and just after return from one shared counter, so real-time order between
    /// threads is exact. Calls still running when <see cref="Snapshot"/> is taken are recorded as
    /// uncertain. Thread-safe; each model thread must call <see cref="Execute"/> from one OS thread
    /// at a time.
    /// </summary>
    public sealed class HistoryRecorder
    {
        private readonly ConcurrentQueue<OperationRecord> _records = new ConcurrentQueue<OperationRecord>();
        private readonly ConcurrentDictionary<int, InFlight> _inFlight = new ConcurrentDictionary<int, InFlight>();
        private readonly ConcurrentDictionary<int, int> _nextIndex = new ConcurrentDictionary<int, int>();
        private long _clock;

        /// <summary>Execute a command of model thread <paramref name="context"/>.Thread through its access kind and record it.</summary>
        public Observation Execute(PropertyCommand command, ThreadContext context)
        {
            var kind = AccessKinds.Get(command.Kind);
            var thread = context.Thread;
            var index = _nextIndex.AddOrUpdate(thread, 0, (_, last) => last + 1);
            var start = Interlocked.Increment(ref _clock);
            _inFlight[thread] = new InFlight(index, command, start);
            Observation observation;
            try
            {
                observation = kind.Execute(command, context);
            }
            catch (Exception ex)
            {
                // An access kind maps engine exceptions itself; anything else is still recorded.
                observation = Observation.UnexpectedException(ex);
            }
            var end = Interlocked.Increment(ref _clock);
            _records.Enqueue(new OperationRecord(thread, index, command, observation, start, end));
            _inFlight.TryRemove(thread, out _);
            return observation;
        }

        /// <summary>Calls that have started and not returned, described for diagnostics.</summary>
        public IReadOnlyList<string> Running() =>
            _inFlight.OrderBy(p => p.Key).Select(p => $"T{p.Key}#{p.Value.Index} {p.Value.Command}").ToList();

        /// <summary>Completed records plus every running call as an uncertain record.</summary>
        public IReadOnlyList<OperationRecord> Snapshot()
        {
            var records = _records.ToList();
            foreach (var pair in _inFlight)
            {
                records.Add(new OperationRecord(pair.Key, pair.Value.Index, pair.Value.Command, Observation.Uncertain, pair.Value.Start, long.MaxValue));
            }
            return records;
        }

        private sealed class InFlight
        {
            public InFlight(int index, PropertyCommand command, long start)
            {
                this.Index = index;
                this.Command = command;
                this.Start = start;
            }

            public int Index { get; }
            public PropertyCommand Command { get; }
            public long Start { get; }
        }
    }
}
