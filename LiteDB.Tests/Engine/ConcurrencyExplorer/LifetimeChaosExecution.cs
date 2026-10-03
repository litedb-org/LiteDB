using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Runs one <see cref="LifetimeChaosProgram"/> on an <see cref="ExplorerRun"/>: every node on its
    /// own actor under its declared deadline, a node's callback starting and awaiting its children
    /// (driver dependency edges in the wait-for graph), the maintenance actor at the program's
    /// trigger. Permitted outcomes follow from what disturbs each node's connection or file
    /// (<see cref="Permitted"/>); acknowledged effects go to the file models for the cold check.
    /// </summary>
    internal sealed partial class LifetimeChaosExecution
    {
        private static readonly byte[] Content = Enumerable.Range(0, 300 * 1024).Select(i => (byte)(i * 13)).ToArray();
        private readonly LifetimeChaosProgram _program;
        private readonly ExplorerRun _run;
        private readonly Dictionary<(int File, int Slot), LiteDatabase> _connections = new Dictionary<(int File, int Slot), LiteDatabase>();
        private readonly ConcurrentDictionary<int, Action> _transforms = new ConcurrentDictionary<int, Action>();
        private readonly ManualResetEventSlim _triggered = new ManualResetEventSlim();
        private readonly ExplorerSchedule.Actor[] _actors;
        private readonly ExplorerSchedule.Work[] _works;
        private readonly CallbackRecord[] _records;
        private readonly bool[] _committed;
        private readonly bool[] _reachedCommit;
        private readonly HashSet<LiteDatabase> _disposedByBody = new HashSet<LiteDatabase>();
        private const int PeerSlot = 9;
        private const int OtherFile = 9;

        internal LifetimeChaosExecution(LifetimeChaosProgram program, ExplorerRun run)
        {
            _program = program;
            _run = run;
            var count = program.Nodes.Count;
            _actors = program.Nodes.Select(node => run.Actor("N" + node.Index)).ToArray();
            _works = new ExplorerSchedule.Work[count];
            _records = program.Nodes.Select(_ => new CallbackRecord()).ToArray();
            _committed = new bool[count];
            _reachedCommit = new bool[count];
        }

        private ExplorerConfiguration C => _program.Configuration;

        private LiteDatabase Connection(int file, int slot)
        {
            lock (_connections)
            {
                if (!_connections.TryGetValue((file, slot), out var db))
                    _connections[(file, slot)] = db = _run.Open(file, this.Transform);
                return db;
            }
        }

        private LiteDatabase Connection(ChaosNode node) => this.Connection(node.File, node.Slot);

        /// <summary>Every connection's ReadTransform: runs the callback armed for the reading thread, once.</summary>
        private BsonValue Transform(string collection, BsonValue value)
        {
            if (_transforms.TryRemove(Thread.CurrentThread.ManagedThreadId, out var callback)) callback();
            return value;
        }

        internal void Run()
        {
            foreach (var node in _program.Nodes)
            {
                this.Connection(node);
                if (node.Body == ChaosBody.Peer) this.Connection(node.File, PeerSlot);
                if (node.Body == ChaosBody.OtherFile) this.Connection(OtherFile, 0);
            }
            var seeded = _program.Nodes.Where(node => node.Op == ChaosOp.FindTransform).ToArray();
            if (seeded.Length > 0)
                _run.A.Run("Seed", () =>
                {
                    foreach (var node in seeded) this.Connection(node).GetCollection(node.Collection).Upsert(ExplorerModel.Row(1, 1));
                });
            foreach (var node in seeded) _run.FileModel(node.File).Acknowledge(node.Collection, 1, 1);
            _run.Schedule.Event("program\n" + _program.Program);

            foreach (var root in _program.Nodes.Where(node => node.Parent < 0)) this.Start(root);
            var maintenance = this.Maintenance();
            var roots = _program.Nodes.Where(node => node.Parent < 0).ToArray();
            foreach (var root in roots) _actors[root.Index].Complete(_works[root.Index]);
            if (maintenance == null) maintenance = this.Maintenance(final: true);
            if (maintenance != null) maintenance.Value.Actor.Complete(maintenance.Value.Work);
            // Children a parent started are awaited by it; one it could not await (it failed) is awaited here.
            foreach (var node in _program.Nodes.Where(node => _works[node.Index] != null)) _actors[node.Index].Complete(_works[node.Index]);

            foreach (var node in _program.Nodes) this.Judge(node);
            if (maintenance != null) maintenance.Value.Account(maintenance.Value.Work);
            LiteDatabase[] disposed;
            lock (_disposedByBody) disposed = _disposedByBody.ToArray();
            foreach (var db in disposed) _run.Disposed(db, "Dispose (from callback)");
        }

        private void Start(ChaosNode node)
        {
            _works[node.Index] = _actors[node.Index].Invoke(node.OpName, () => this.Execute(node),
                node.HasCallback ? ExplorerSchedule.Extended : node.Op == ChaosOp.Checkpoint ? ExplorerSchedule.Extended : ExplorerSchedule.LockBound);
        }

        private void Execute(ChaosNode node)
        {
            var db = this.Connection(node);
            var unit = node.Op == ChaosOp.Checkpoint || node.Op == ChaosOp.Read ? null : _run.Access.Begin(db);
            var completed = false;
            try
            {
                Action callback = () => this.Callback(node, db);
                switch (node.Op)
                {
                    case ChaosOp.InsertInput:
                        unit.Collection(node.Collection).Insert(ExplorerRun.Input(null, callback, node.Teardown, 20 + node.Index));
                        break;
                    case ChaosOp.FindTransform:
                        _transforms[Thread.CurrentThread.ManagedThreadId] = callback;
                        try { unit.Collection(node.Collection).FindAll().ToList(); }
                        finally { _transforms.TryRemove(Thread.CurrentThread.ManagedThreadId, out _); }
                        break;
                    case ChaosOp.Upload:
                        db.FileStorage.Upload("$/chaos/" + node.Collection, node.Collection + ".bin", new PausingStream(Content, null, callback));
                        break;
                    case ChaosOp.Read:
                        db.GetCollection("sentinel").FindById(42);
                        break;
                    case ChaosOp.Write:
                        unit.Collection(node.Collection).Upsert(ExplorerModel.Row(1, 100 + node.Index));
                        break;
                    case ChaosOp.Checkpoint:
                        db.Checkpoint();
                        break;
                }
                if (unit != null)
                {
                    _reachedCommit[node.Index] = true;
                    if (node.Commit || !unit.Transactional) _committed[node.Index] = unit.Commit();
                    else unit.Rollback();
                }
                completed = true;
            }
            finally
            {
                // After a failure the unit's cleanup must not replace the operation's own error.
                try { unit?.Dispose(); }
                catch (Exception) when (!completed) { }
            }
        }

        private void Callback(ChaosNode node, LiteDatabase db)
        {
            var record = _records[node.Index];
            record.Ran = true;
            if (_program.TriggerNode == node.Index) _triggered.Set();
            _run.Schedule.Event("N" + node.Index + " callback " + ExplorerConfiguration.Name(node.Body));
            if (node.Body == ChaosBody.Await)
            {
                this.Await(node);
                return;
            }
            var refusals = ExplorerRefusals.OnThread;
            try
            {
                switch (node.Body)
                {
                    case ChaosBody.SameConnection:
                        record.Collection = "s" + node.Index;
                        Reachability.Sometimes("situation:explorer-same-connection-call-from-callback");
                        db.GetCollection("sentinel").FindById(42);
                        db.GetCollection(record.Collection).Upsert(ExplorerModel.Row(5, 50));
                        break;
                    case ChaosBody.Peer:
                        record.Collection = "p" + node.Index;
                        record.Independent = true;
                        Reachability.Sometimes("situation:explorer-peer-call-from-callback");
                        this.Connection(node.File, PeerSlot).GetCollection(record.Collection).Upsert(ExplorerModel.Row(5, 50));
                        break;
                    case ChaosBody.OtherFile:
                        record.Collection = "o" + node.Index;
                        record.Independent = record.OtherFile = true;
                        this.Connection(OtherFile, 0).GetCollection(record.Collection).Upsert(ExplorerModel.Row(5, 50));
                        break;
                    case ChaosBody.Dispose:
                        db.Dispose();
                        _run.MarkDisposed(db);
                        lock (_disposedByBody) _disposedByBody.Add(db);
                        Reachability.Sometimes("situation:explorer-callback-disposed-its-own-connection");
                        break;
                }
            }
            catch (Exception error)
            {
                record.Error = error;
                record.Refused = ExplorerRefusals.OnThread != refusals;
            }
            _run.Schedule.Event("N" + node.Index + " callback outcome " + ExplorerJudge.Describe(record.Error));
        }

        /// <summary>The callback starts its children on their actors and waits for each (a driver dependency edge).</summary>
        private void Await(ChaosNode node)
        {
            Reachability.Sometimes("situation:explorer-callback-awaits-another-thread");
            var children = node.Children.Select(index => _program.Nodes[index]).ToArray();
            if (!node.Sequential) foreach (var child in children) this.Start(child);
            foreach (var child in children)
            {
                if (node.Sequential) this.Start(child);
                var work = _works[child.Index];
                var awaited = _run.Schedule.Edge(_actors[child.Index].Name + "/" + work.Name);
                var clock = Stopwatch.StartNew();
                using (ExplorerDriverEdges.Dependency("N" + node.Index, awaited, ExplorerSchedule.ControllerBound))
                {
                    while (!work.Done.Wait(1))
                    {
                        ExplorerDriverEdges.Recheck();
                        if (clock.Elapsed >= ExplorerSchedule.ControllerBound)
                            throw new ExplorerFailure("EXPLORER_DEPENDENCY_TIMEOUT", "N" + node.Index + " awaited N" + child.Index + " past the harness bound");
                    }
                }
            }
        }

        /// <summary>Starts the maintenance actor when the trigger fires (null when the trigger is final and not reached yet).</summary>
        private (ExplorerSchedule.Actor Actor, ExplorerSchedule.Work Work, Action<ExplorerSchedule.Work> Account)? Maintenance(bool final = false)
        {
            if (C.Maintenance == ExplorerMaintenance.None) return null;
            if (_program.Trigger == ChaosTrigger.Final && !final) return null;
            if (_program.Trigger == ChaosTrigger.Callback)
                _run.Schedule.Until(() => _triggered.IsSet || _program.Nodes.Where(node => node.Parent < 0).All(node => _works[node.Index].Done.IsSet),
                    "trigger N" + _program.TriggerNode);
            else if (_program.Trigger == ChaosTrigger.Delay) Thread.Sleep(_program.DelayMs);
            _run.Schedule.Decision("maintenance " + ExplorerConfiguration.Name(C.Maintenance) + " after " + ExplorerConfiguration.Name(_program.Trigger));
            var target = _program.Nodes[_program.MaintenanceNode];
            var db = this.Connection(target);
            var actor = _run.Actor("M");
            switch (C.Maintenance)
            {
                case ExplorerMaintenance.Close:
                    return (actor, actor.Invoke("Dispose", () => _run.Dispose(db), ExplorerSchedule.Extended), work =>
                    {
                        _run.Judge(work, Permit.Success | Permit.Refusal);
                        Reachability.Sometimes("situation:explorer-close-under-dependencies");
                    });
                case ExplorerMaintenance.Rebuild:
                    return (actor, actor.Invoke("Rebuild", () => db.Rebuild(), ExplorerSchedule.Extended), work =>
                    {
                        _run.Judge(work, Permit.Disposed | Permit.Refusal | Permit.LockTimeout);
                        Reachability.Sometimes("situation:explorer-rebuild-under-dependencies");
                    });
                default:
                    var fault = _run.Keep(_run.ArmFatal(db));
                    var model = _run.FileModel(target.File);
                    return (actor, actor.Invoke("Write.fatal", () =>
                    {
                        fault.Thread = Thread.CurrentThread;
                        db.GetCollection("m").Upsert(ExplorerModel.Row(1, 1));
                    }), work =>
                    {
                        _run.Host.FaultReached("SimulateDiskWriteFail", fault.Injected, required: work.Ok);
                        _run.Host.FaultDisposed("Write.fatal", fault.Injected, FaultDisposition.Propagated, work.Failure);
                        _run.Judge(work, Permit.Fatal | Permit.Disposed | Permit.Refusal | Permit.LockTimeout);
                        if (work.Ok) model.Acknowledge("m", 1, 1);
                        else model.Uncertain("m", 1, 1);
                        Reachability.Sometimes("situation:explorer-fatal-under-dependencies");
                    });
            }
        }
    }
}
