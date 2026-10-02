using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Dedicated actors, forced boundaries and independent operation deadlines.
    /// Ported from JKamsker/LiteDB#133 for litedb-org/LiteDB#3064.
    /// </summary>
    internal sealed class ExplorerSchedule : IDisposable
    {
        private readonly List<Actor> _actors = new List<Actor>();
        private readonly List<Boundary> _boundaries = new List<Boundary>();
        private readonly object _logGate = new object();
        private readonly StreamWriter _log;
        private int _sequence;
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

        internal ExplorerSchedule(string path, string configuration)
        {
            _log = new StreamWriter(path) { AutoFlush = true };
            Event("configuration " + configuration);
        }

        internal void Event(string value)
        {
            lock (_logGate) _log.WriteLine(++_sequence + " " + Stopwatch.GetTimestamp() + " " + value);
        }

        internal Actor NewActor(string name)
        {
            var actor = new Actor(this, name);
            _actors.Add(actor);
            return actor;
        }

        internal Boundary NewBoundary(string name)
        {
            var boundary = new Boundary(this, name);
            _boundaries.Add(boundary);
            return boundary;
        }

        internal void Until(Func<bool> condition, string reason)
        {
            // Poll observable state only; actor failures and deadlines are checked between polls.
            var started = Stopwatch.StartNew();
            while (!SpinWait.SpinUntil(condition, 10))
            {
                CheckActors();
                if (started.Elapsed >= Deadline) throw new TimeoutException("Boundary not reached: " + reason);
            }
            CheckActors();
            Event("reached " + reason);
        }

        internal void CheckActors()
        {
            foreach (var actor in _actors)
            {
                var work = actor.Current;
                if (work == null) continue;
                if (work.Failure != null) throw new InvalidOperationException(actor.Name + ": " + work.Name, work.Failure);
                var completed = Interlocked.Read(ref work.Completed);
                if ((completed == 0 ? Stopwatch.GetTimestamp() : completed) - work.Started > Deadline.TotalSeconds * Stopwatch.Frequency)
                    throw new TimeoutException("Worker stalled: " + actor.Name + "/" + work.Name);
            }
        }

        internal bool Stop()
        {
            foreach (var boundary in _boundaries) boundary.Release();
            foreach (var actor in _actors) actor.Stop();
            var stopped = true;
            foreach (var actor in _actors) stopped &= actor.Join();
            Event(stopped ? "all-workers-joined" : "LIVE-WORKER fixture retained; no cleanup or cold inspection");
            return stopped;
        }

        public void Dispose()
        {
            // The caller must not close this log while a live actor can still report.
            _log.Dispose();
        }

        internal sealed class Work
        {
            internal readonly string Name;
            internal readonly long Started = Stopwatch.GetTimestamp();
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            internal Exception Failure;
            internal long Completed;
            internal Work(string name) { Name = name; }
        }

        internal sealed class Actor
        {
            private readonly ExplorerSchedule _schedule;
            private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
            private readonly Thread _thread;
            internal readonly string Name;
            internal volatile Work Current;

            internal Actor(ExplorerSchedule schedule, string name)
            {
                _schedule = schedule;
                Name = name;
                _thread = new Thread(() =>
                {
                    foreach (var action in _queue.GetConsumingEnumerable()) action();
                }) { IsBackground = true, Name = "concurrency-explorer-" + name };
                _thread.Start();
            }

            internal Work Invoke(string name, Action action)
            {
                _schedule.CheckActors();
                if (Current != null && !Current.Done.IsSet) throw new InvalidOperationException("Actor already busy: " + Name);
                if (Current?.Failure != null) throw new InvalidOperationException("Previous actor failure", Current.Failure);
                var work = Current = new Work(name);
                _schedule.Event(Name + " invoke " + name);
                _queue.Add(() =>
                {
                    try { action(); _schedule.Event(Name + " complete " + name); }
                    catch (Exception error)
                    {
                        work.Failure = error;
                        _schedule.Event(Name + " error " + name + " " + error);
                    }
                    finally { Interlocked.Exchange(ref work.Completed, Stopwatch.GetTimestamp()); work.Done.Set(); }
                });
                return work;
            }

            internal void Complete(Work work) => _schedule.Until(() => work.Done.IsSet, Name + "/" + work.Name);
            internal void Run(string name, Action action) => Complete(Invoke(name, action));
            internal void Stop() => _queue.CompleteAdding();
            internal bool Join() => _thread.Join(Deadline);
        }

        internal sealed class Boundary
        {
            private readonly ExplorerSchedule _schedule;
            private readonly string _name;
            private readonly ManualResetEventSlim _reached = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
            private int _released;
            internal Boundary(ExplorerSchedule schedule, string name) { _schedule = schedule; _name = name; }
            internal void Hit()
            {
                _schedule.Event("boundary " + _name);
                _reached.Set();
                if (!_release.Wait(Deadline)) throw new TimeoutException("Unreleased boundary " + _name);
            }
            internal void Observe() { _schedule.Event("observed " + _name); _reached.Set(); }
            internal void Wait() => _schedule.Until(() => _reached.IsSet, _name);
            internal void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                _schedule.Event("release " + _name); _release.Set();
            }
        }
    }
}
