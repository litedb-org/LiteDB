using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Tests.Safety;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Dedicated actor threads, forced boundaries and per-operation deadlines (ported from the
    /// PR #133 explorer and generalized). The controller (the thread that owns the schedule) makes
    /// every scheduling decision; each decision is recorded in order, so a schedule vector replays
    /// the same decisions (evidence class 1). Each actor operation runs under the deadline its
    /// scenario declares, through the host (M1 Deadline semantics: the clock starts when the
    /// operation starts on its actor and only that operation's completion ends it).
    /// </summary>
    internal sealed class ExplorerSchedule : IDisposable
    {
        /// <summary>The engine TIMEOUT pragma the explorer's connections use.</summary>
        internal static readonly TimeSpan PragmaTimeout = TimeSpan.FromSeconds(5);
        /// <summary>Default for lock-bound operations: max(3 x TIMEOUT, 15 s).</summary>
        internal static readonly TimeSpan LockBound = DeadlineWatchdog.LockBound(PragmaTimeout);
        /// <summary>Declared for operations whose callback holds a forced boundary, rebuild and close.</summary>
        internal static readonly TimeSpan Extended = TimeSpan.FromTicks(LockBound.Ticks * 4);
        /// <summary>How long a controller wait or an unreleased boundary may last (a harness bound).</summary>
        internal static readonly TimeSpan ControllerBound = Extended + TimeSpan.FromSeconds(5);

        private readonly List<Actor> _actors = new List<Actor>();
        private readonly List<Boundary> _boundaries = new List<Boundary>();
        private readonly List<string> _decisions = new List<string>();
        private readonly List<Work> _works = new List<Work>();
        private readonly object _logGate = new object();
        private readonly StreamWriter _log;
        private static int _schedules;
        private readonly int _id = Interlocked.Increment(ref _schedules);
        private int _sequence;

        internal ExplorerSchedule(string historyPath, string header, IExplorerHost host, string dimension)
        {
            this.Host = host;
            this.Dimension = dimension;
            this.HistoryPath = historyPath;
            _log = new StreamWriter(historyPath) { AutoFlush = true };
            this.Event("configuration " + header);
        }

        internal IExplorerHost Host { get; }
        internal string Dimension { get; }
        internal string HistoryPath { get; }

        internal IReadOnlyList<string> Decisions
        {
            get { lock (_logGate) return _decisions.ToArray(); }
        }

        internal void Event(string value)
        {
            lock (_logGate)
            {
                if (_log.BaseStream != null) _log.WriteLine(++_sequence + " " + Stopwatch.GetTimestamp() + " " + value);
            }
        }

        /// <summary>A scheduling decision of the controller; part of the replayable vector.</summary>
        internal void Decision(string value)
        {
            lock (_logGate) _decisions.Add(value);
            this.Event("decision " + value);
        }

        private void Track(Work work)
        {
            lock (_logGate) _works.Add(work);
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

        /// <summary>A name unique to this schedule for the wait-for graph's driver edges.</summary>
        internal string Edge(string name) => _id + ":" + name;

        internal void Until(Func<bool> condition, string reason, string awaited = null)
        {
            var started = Stopwatch.StartNew();
            using (awaited == null ? null : ExplorerDriverEdges.Controller(this.Edge(awaited), ControllerBound))
            {
                while (!condition())
                {
                    ExplorerDriverEdges.Recheck();
                    this.CheckActors();
                    if (started.Elapsed >= ControllerBound)
                        throw new ExplorerFailure("EXPLORER_CONTROLLER_TIMEOUT", "condition not reached: " + reason);
                    Thread.Sleep(1);
                }
            }
            this.CheckActors();
        }

        /// <summary>Raises the first operation that missed its deadline.</summary>
        internal void CheckActors()
        {
            var overdue = this.Host.Overdue();
            if (overdue != null) throw overdue;
        }

        /// <summary>Operations that failed and that no scenario judged (a scenario must judge every outcome).</summary>
        internal IEnumerable<Work> Unjudged()
        {
            lock (_logGate) return _works.Where(work => work.Done.IsSet && work.Failure != null && !work.Judged).ToArray();
        }

        /// <summary>Releases every boundary, stops and joins the actors; false when one is still live.</summary>
        internal bool Stop(bool failed = false)
        {
            foreach (var boundary in _boundaries) boundary.Release();
            foreach (var actor in _actors) actor.Stop();
            // After a failure an actor may be stuck for good: one short shared bound, not one per actor.
            var deadline = Stopwatch.StartNew();
            var bound = failed ? TimeSpan.FromSeconds(5) : ControllerBound;
            var stopped = true;
            foreach (var actor in _actors)
            {
                var left = bound - deadline.Elapsed;
                stopped &= actor.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            this.Event(stopped ? "all-workers-joined" : "LIVE-WORKER fixture retained; no cleanup or cold inspection");
            return stopped;
        }

        public void Dispose()
        {
            lock (_logGate) _log.Dispose();
        }

        internal sealed class Work
        {
            internal readonly string Name;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            internal Exception Failure;
            /// <summary>A refusal marker fired on the actor thread during the failed operation.</summary>
            internal bool Refused;
            /// <summary>The scenario judged this outcome against its permitted outcomes.</summary>
            internal bool Judged;
            internal Work(string actor, string name) { this.Actor = actor; this.Name = name; }
            internal string Actor { get; }
            internal bool Ok => this.Done.IsSet && this.Failure == null;
            public override string ToString() => this.Actor + "/" + this.Name;
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
                this.Name = name;
                _thread = new Thread(() =>
                {
                    foreach (var action in _queue.GetConsumingEnumerable()) action();
                }) { IsBackground = true, Name = "concurrency-explorer-" + name };
                _thread.Start();
            }

            internal Thread Thread => _thread;

            /// <summary>Starts <paramref name="op"/> (a stable operation class) on this actor under its declared deadline.</summary>
            internal Work Invoke(string op, Action action, TimeSpan? deadline = null)
            {
                if (this.Current != null && !this.Current.Done.IsSet) throw new InvalidOperationException("Actor already busy: " + this.Name);
                var work = this.Current = new Work(this.Name, op);
                _schedule.Track(work);
                _schedule.Decision(this.Name + " invoke " + op);
                _queue.Add(() =>
                {
                    ExplorerDriverEdges.Running(_schedule.Edge(this.Name + "/" + op));
                    var refusals = ExplorerRefusals.OnThread;
                    try
                    {
                        _schedule.Host.Execute(op, _schedule.Dimension, deadline ?? LockBound, action);
                        _schedule.Event(this.Name + " complete " + op);
                    }
                    catch (Exception error)
                    {
                        work.Failure = error;
                        work.Refused = ExplorerRefusals.OnThread != refusals;
                        _schedule.Event(this.Name + " error " + op + " " + error.GetType().FullName + ": " + error.Message);
                    }
                    finally
                    {
                        ExplorerDriverEdges.Finished(_schedule.Edge(this.Name + "/" + op));
                        work.Done.Set();
                    }
                });
                return work;
            }

            internal void Complete(Work work)
            {
                _schedule.Decision("await " + this.Name + "/" + work.Name);
                _schedule.Until(() => work.Done.IsSet, this.Name + "/" + work.Name, this.Name + "/" + work.Name);
            }

            internal void Run(string op, Action action, TimeSpan? deadline = null) => this.Complete(this.Invoke(op, action, deadline));

            internal void Stop() => _queue.CompleteAdding();

            internal bool Join(TimeSpan bound)
            {
                using (ExplorerDriverEdges.Join(_thread, bound)) return _thread.Join(bound);
            }
        }

        internal sealed class Boundary
        {
            private readonly ExplorerSchedule _schedule;
            private readonly ManualResetEventSlim _reached = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
            private int _released;

            internal Boundary(ExplorerSchedule schedule, string name)
            {
                _schedule = schedule;
                this.Name = name;
                ExplorerDriverEdges.Owe(schedule.Edge(name));
            }

            internal string Name { get; }
            internal bool Reached => _reached.IsSet;

            /// <summary>On an actor: mark reached, then block until the controller releases it.</summary>
            internal void Hit()
            {
                _schedule.Event("boundary " + this.Name);
                _reached.Set();
                using (ExplorerDriverEdges.Boundary(_schedule.Edge(this.Name), ControllerBound))
                {
                    if (!_release.Wait(ControllerBound))
                        throw new ExplorerFailure("EXPLORER_UNRELEASED_BOUNDARY", "boundary never released: " + this.Name);
                }
            }

            /// <summary>On an actor: mark reached without blocking.</summary>
            internal void Observe()
            {
                _schedule.Event("observed " + this.Name);
                _reached.Set();
            }

            internal void Wait()
            {
                _schedule.Decision("wait " + this.Name);
                _schedule.Until(() => _reached.IsSet, this.Name);
            }

            internal void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                _schedule.Event("release " + this.Name);
                ExplorerDriverEdges.Paid(_schedule.Edge(this.Name));
                _release.Set();
            }

            /// <summary>A controller decision to release (recorded); <see cref="Release"/> alone is cleanup.</summary>
            internal void Let()
            {
                _schedule.Decision("release " + this.Name);
                this.Release();
            }
        }
    }
}
