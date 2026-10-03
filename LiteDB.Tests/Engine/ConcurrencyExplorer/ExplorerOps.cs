using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>What a user callback did when it ran (see <see cref="ExplorerRun.Callback"/>).</summary>
    internal sealed class CallbackRecord
    {
        public bool Ran;
        public Exception Error;
        /// <summary>A refusal marker fired on the callback's thread during its nested call.</summary>
        public bool Refused;
        /// <summary>The write the callback attempted: collection, id, value, and whether it targets the other file.</summary>
        public string Collection;
        public int Id;
        public int Value;
        public bool OtherFile;
        public bool Independent;
    }

    /// <summary>A fatal fault armed on one connection: the next WAL page write on <see cref="Thread"/> throws.</summary>
    internal sealed class FatalFault : IDisposable
    {
        private readonly Action _disarm;
        private Exception _injected;

        internal FatalFault(Action<FatalFault> arm, Action disarm)
        {
            _disarm = disarm;
            arm(this);
        }

        internal Thread Thread;
        internal Exception Injected => Volatile.Read(ref _injected);

        /// <summary>The injector (registered fault point SimulateDiskWriteFail, model fail-inside-skip: the page write never happens).</summary>
        internal void Hook(PageBuffer page)
        {
            if (!ReferenceEquals(Thread.CurrentThread, this.Thread) || this.Injected != null) return;
            var injected = new IOException("explorer: injected fatal WAL write failure");
            if (Interlocked.CompareExchange(ref _injected, injected, null) == null) throw injected;
        }

        public void Dispose() => _disarm();
    }

    internal sealed partial class ExplorerRun
    {
        /// <summary>
        /// The body a user callback runs on the callback dimension: nothing; a read and a write on the
        /// same connection; a write on a second connection to the same file (peer); a write on a
        /// connection to another file (positive control); or Dispose of its own connection. The
        /// nested call's exception is recorded, not rethrown, so the outer operation proceeds.
        /// </summary>
        internal Action Callback(LiteDatabase self, LiteDatabase peer, LiteDatabase other, CallbackRecord record)
        {
            var callback = this.Configuration.Callback;
            record.Id = 5;
            record.Value = 50;
            switch (callback)
            {
                case ExplorerCallback.None: return () => record.Ran = true;
                case ExplorerCallback.SameConnection: record.Collection = "other"; break;
                case ExplorerCallback.Peer: record.Collection = "other"; record.Independent = true; break;
                case ExplorerCallback.OtherFile: record.Collection = "rows"; record.OtherFile = record.Independent = true; break;
                case ExplorerCallback.Dispose: break;
            }
            return () =>
            {
                record.Ran = true;
                this.Schedule.Event("callback " + ExplorerConfiguration.Name(callback) + " runs on " + Thread.CurrentThread.Name);
                var refusals = ExplorerRefusals.OnThread;
                try
                {
                    switch (callback)
                    {
                        case ExplorerCallback.SameConnection:
                            self.GetCollection("sentinel").FindById(42);
                            self.GetCollection("other").Upsert(ExplorerModel.Row(record.Id, record.Value));
                            break;
                        case ExplorerCallback.Peer:
                            peer.GetCollection("other").Upsert(ExplorerModel.Row(record.Id, record.Value));
                            break;
                        case ExplorerCallback.OtherFile:
                            other.GetCollection("rows").Upsert(ExplorerModel.Row(record.Id, record.Value));
                            break;
                        case ExplorerCallback.Dispose:
                            self.Dispose();
                            this.MarkDisposed(self);
                            break;
                    }
                }
                catch (Exception error)
                {
                    record.Error = error;
                    record.Refused = ExplorerRefusals.OnThread != refusals;
                }
                this.Schedule.Event("callback outcome " + ExplorerJudge.Describe(record.Error));
            };
        }

        /// <summary>
        /// Applies a callback's write to the models: an independent connection's write is acknowledged
        /// when it returned; a same-connection write joins the outer operation, so it is acknowledged
        /// with it (<paramref name="outerAcknowledged"/>) and uncertain otherwise.
        /// </summary>
        internal void Account(CallbackRecord record, bool outerAcknowledged)
        {
            if (!record.Ran || record.Collection == null) return;
            var model = record.OtherFile ? this.Other : this.Model;
            if (record.Error == null && (record.Independent || outerAcknowledged)) model.Acknowledge(record.Collection, record.Id, record.Value);
            else model.Uncertain(record.Collection, record.Id, record.Value);
        }

        /// <summary>
        /// Arms a fatal fault (registered injector SimulateDiskWriteFail) on <paramref name="db"/> for the
        /// thread that will run the faulting write. Direct: the connection's engine. Shared: every
        /// core the connection opens while armed.
        /// </summary>
        internal FatalFault ArmFatal(LiteDatabase db)
        {
            var engine = Tests.Safety.ConnectionCleanProbe.EngineOf(db);
            if (engine is LiteEngine direct)
                return new FatalFault(fault => direct.SimulateDiskWriteFail = fault.Hook, () => direct.SimulateDiskWriteFail = null);
            var shared = (SharedEngine)engine;
            // The core attached now (an operation in flight may be using it) and every core opened while armed.
            var attached = typeof(SharedEngine).GetField("_engine", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.GetValue(shared) as LiteEngine;
            Action<SharedEngine, LiteEngine, string> stage = null;
            return new FatalFault(fault =>
            {
                stage = (connection, core, name) =>
                {
                    if (ReferenceEquals(connection, shared) && name == "opened") core.SimulateDiskWriteFail = fault.Hook;
                };
                Client.Shared.SharedOwnershipEvents.CoreStage += stage;
                if (attached != null) attached.SimulateDiskWriteFail = fault.Hook;
            }, () =>
            {
                Client.Shared.SharedOwnershipEvents.CoreStage -= stage;
                if (attached != null) attached.SimulateDiskWriteFail = null;
            });
        }

        /// <summary>A lazy input sequence that pauses at <paramref name="pause"/> (if any) and runs <paramref name="callback"/>.</summary>
        internal static IEnumerable<BsonDocument> Input(ExplorerSchedule.Boundary pause, Action callback, bool inFinally, int value = 20)
        {
            try
            {
                yield return ExplorerModel.Row(2, value);
                if (!inFinally)
                {
                    pause?.Hit();
                    callback();
                }
                yield return ExplorerModel.Row(3, value + 10);
            }
            finally
            {
                // Runs when the engine disposes the enumerator: a callback during the operation's own teardown.
                if (inFinally)
                {
                    pause?.Hit();
                    callback();
                }
            }
        }

        /// <summary>A forced edge: wait until <paramref name="work"/> finished or <paramref name="settle"/> passed (recorded as an observation).</summary>
        internal void Settle(ExplorerSchedule.Work work, TimeSpan settle)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (!work.Done.IsSet && started.Elapsed < settle)
            {
                this.Schedule.CheckActors();
                Thread.Sleep(1);
            }
            this.Schedule.Event("observed " + work + (work.Done.IsSet ? " finished" : " still running") + " before the next decision");
        }

        internal void Judge(ExplorerSchedule.Work work, Permit permitted)
        {
            ExplorerJudge.Judge(this.Schedule, work, permitted);
            this.CheckOwnership("after " + work);
        }

        /// <summary>
        /// Judges what the callback's nested call did: a nested call on the same connection, a peer
        /// connection or a Dispose of its own connection may be refused or give up a bounded wait;
        /// it must never hang (its operation's deadline) and never fail otherwise. Plus whatever
        /// <paramref name="disturbance"/> the configuration races.
        /// </summary>
        internal void JudgeCallback(CallbackRecord record, string where, Permit disturbance)
        {
            var nested = this.Configuration.Callback == ExplorerCallback.None || this.Configuration.Callback == ExplorerCallback.OtherFile
                ? Permit.Success : Permit.Refusal | Permit.LockTimeout;
            this.Schedule.Event("outcome callback " + ExplorerJudge.Describe(record.Error) + (record.Refused ? " (refusal marker)" : ""));
            ExplorerJudge.Judge(where, "Callback", record.Error, nested | disturbance, record.Refused);
        }

        internal void Mark(string situation) => this.Schedule.Event("situation " + situation);
    }
}
