using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// One explorer run: a schedule vector executed by one scenario against fresh fixture files,
    /// with five dedicated actors (A owns the operation under test; B, C, D contend; E takes handoffs), connection
    /// bookkeeping for the oracles, and the shell of the JKamsker/LiteDB#133 explorer: actor checks,
    /// stop/join, live-fixture retention, reverse disposal with ConnectionClean, the independent
    /// cold check (Durable), Quiescent, and a failure artifact. Entry point: <see cref="Execute"/>.
    /// </summary>
    internal sealed partial class ExplorerRun
    {
        private readonly List<IDisposable> _resources = new List<IDisposable>();
        private readonly List<LiteDatabase> _connections = new List<LiteDatabase>();
        private readonly HashSet<LiteDatabase> _disposed = new HashSet<LiteDatabase>();
        private readonly HashSet<LiteDatabase> _cleaned = new HashSet<LiteDatabase>();
        private ExplorerModel _other;
        private readonly SortedDictionary<int, ExplorerModel> _files = new SortedDictionary<int, ExplorerModel>();

        private ExplorerRun(ExplorerVector vector, string directory, IExplorerHost host, IExplorerAccess access)
        {
            this.Vector = vector;
            this.Directory = directory;
            this.Host = host;
            this.Access = access;
            System.IO.Directory.CreateDirectory(directory);
            ExplorerRefusals.Install();
            this.Schedule = new ExplorerSchedule(Path.Combine(directory, "explorer.history"), vector.ToString(), host,
                vector.Configuration.Signature);
            if (this.Configuration.Shared) host.WatchOwnership();
            this.Model = new ExplorerModel(Path.Combine(directory, "explorer.db"), this.Configuration.Mode, this.Configuration.Encrypted);
            this.A = this.Schedule.NewActor("A");
            this.B = this.Schedule.NewActor("B");
            this.C = this.Schedule.NewActor("C");
            this.D = this.Schedule.NewActor("D");
            this.E = this.Schedule.NewActor("E");
        }

        internal ExplorerVector Vector { get; }
        internal ExplorerConfiguration Configuration => this.Vector.Configuration;
        internal string Directory { get; }
        internal IExplorerHost Host { get; }
        internal IExplorerAccess Access { get; }
        internal ExplorerSchedule Schedule { get; }
        internal ExplorerModel Model { get; }
        internal ExplorerSchedule.Actor A { get; }
        internal ExplorerSchedule.Actor B { get; }
        internal ExplorerSchedule.Actor C { get; }
        internal ExplorerSchedule.Actor D { get; }
        /// <summary>A fifth thread for sequential handoffs (a reader closed on a thread that did not open or advance it).</summary>
        internal ExplorerSchedule.Actor E { get; }
        internal ExternalWriter External { get; private set; }

        /// <summary>The model of a second database file (other-file callbacks are its positive control).</summary>
        internal ExplorerModel Other => _other ?? (_other = new ExplorerModel(Path.Combine(this.Directory, "explorer-other.db"),
            this.Configuration.Mode, this.Configuration.Encrypted));

        /// <summary>
        /// The model of database file <paramref name="index"/>: 0 is the fixture (<see cref="Model"/>), 1 is
        /// <see cref="Other"/>, higher indexes are further files (dependency chains that must not share a file).
        /// </summary>
        internal ExplorerModel FileModel(int index)
        {
            if (index == 0) return this.Model;
            if (index == 1) return this.Other;
            lock (_files)
            {
                if (!_files.TryGetValue(index, out var model))
                    _files[index] = model = new ExplorerModel(Path.Combine(this.Directory, "explorer-f" + index + ".db"),
                        this.Configuration.Mode, this.Configuration.Encrypted);
                return model;
            }
        }

        /// <summary>A new tracked connection to the fixture (or to <see cref="Other"/>).</summary>
        internal LiteDatabase Open(Func<string, BsonValue, BsonValue> readTransform = null, bool other = false) =>
            this.Open(other ? 1 : 0, readTransform);

        /// <summary>A new tracked connection to file <paramref name="file"/> (see <see cref="FileModel"/>).</summary>
        internal LiteDatabase Open(int file, Func<string, BsonValue, BsonValue> readTransform = null)
        {
            var db = this.FileModel(file).Open(readTransform);
            lock (_connections) _connections.Add(db);
            return db;
        }

        /// <summary>An extra actor beyond A-E (dependency programs use up to seven threads).</summary>
        internal ExplorerSchedule.Actor Actor(string name) => this.Schedule.NewActor(name);

        internal T Keep<T>(T resource) where T : IDisposable
        {
            lock (_resources) _resources.Add(resource);
            return resource;
        }

        internal bool IsDisposed(LiteDatabase db)
        {
            lock (_connections) return _disposed.Contains(db);
        }

        /// <summary>Dispose <paramref name="db"/> on the calling actor, then ConnectionClean.</summary>
        internal void Dispose(LiteDatabase db, string op = "Dispose")
        {
            db.Dispose();
            this.MarkDisposed(db);
            this.Disposed(db, op);
        }

        /// <summary>Bookkeeping only: <paramref name="db"/>'s Dispose returned (a callback may call this while its operation is still running).</summary>
        internal void MarkDisposed(LiteDatabase db)
        {
            lock (_connections) _disposed.Add(db);
        }

        /// <summary>Records a disposed connection (once) and runs ConnectionClean for it, after every operation on it finished.</summary>
        internal void Disposed(LiteDatabase db, string op)
        {
            lock (_connections)
            {
                _disposed.Add(db);
                if (!_cleaned.Add(db)) return;
            }
            this.Host.ConnectionClean(db, op);
        }

        /// <summary>The Ownership oracle for every live Shared connection (after each operation, on the controller).</summary>
        internal void CheckOwnership(string point)
        {
            if (!this.Configuration.Shared) return;
            LiteDatabase[] live;
            lock (_connections) live = _connections.Where(db => !_disposed.Contains(db)).ToArray();
            foreach (var db in live)
                this.Host.Ownership(Tests.Safety.ConnectionCleanProbe.EngineOf(db) as SharedEngine, point);
            if (live.Length == 0) this.Host.Ownership(null, point);
        }

        /// <summary>The stable id an oracle exception of a host carries (a FailureId property, often internal), or null.</summary>
        internal static string CarriedFailureId(Exception error) => error?.GetType().GetProperty("FailureId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(error) as string;

        internal static string DefaultFailureId(Exception error)
        {
            switch (error)
            {
                case ExplorerFailure failure: return failure.Id;
                case null: return "UNKNOWN";
                default:
                    return CarriedFailureId(error) ?? "UNEXPECTED_EXCEPTION_" + ExplorerFailure.Safe(error.GetType().Name);
            }
        }

        /// <summary>The six orders of three contenders (the JKamsker/LiteDB#133 permutation table).</summary>
        internal static readonly int[][] Permutations =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };

        /// <summary>
        /// Runs <paramref name="vector"/> in <paramref name="directory"/> (a fresh directory per run).
        /// NotApplicable when the access kind is missing from this build, or the scenario does not
        /// support the configuration; never a pass.
        /// </summary>
        /// <param name="program">A generated scenario (lifetime-chaos) instead of the registered one named by the vector.</param>
        internal static ExplorerResult Execute(ExplorerVector vector, string directory, IExplorerHost host, IExplorerScenario program = null)
        {
            var result = new ExplorerResult { Vector = vector, Directory = directory };
            var reason = ExplorerAccessKinds.NotApplicableReason(vector.Configuration.Access);
            var scenario = program ?? ExplorerScenarios.Find(vector.Scenario);
            if (scenario is IExplorerEvidence evidence)
            {
                result.EvidenceClass = evidence.EvidenceClass;
                result.Program = evidence.Program;
            }
            var access = ExplorerAccessKinds.Find(vector.Configuration.Access);
            if (reason == null && scenario == null) reason = "unknown scenario '" + vector.Scenario + "'";
            if (reason == null) reason = DimensionRule(vector.Configuration) ?? scenario.NotApplicable(vector.Configuration, access);
            if (reason == null && vector.Configuration.Process == ExplorerProcess.ExternalWriter)
                reason = ExternalWriter.NotApplicable(vector.Configuration, host);
            if (reason != null)
            {
                result.Verdict = ExplorerVerdict.NotApplicable;
                result.NotApplicableReason = reason;
                return result;
            }
            var run = new ExplorerRun(vector, directory, host, access);
            result.HistoryPath = run.Schedule.HistoryPath;
            Exception failure = null;
            try
            {
                if (vector.Configuration.Process == ExplorerProcess.ExternalWriter)
                    run.External = run.Keep(ExternalWriter.Start(host, run.Model));
                scenario.Run(run, vector.Variant);
                run.Schedule.CheckActors();
                run.CheckOwnership("scenario end");
            }
            catch (Exception error)
            {
                failure = error;
                run.Schedule.Event("FAIL " + error);
            }
            failure = run.Finish(failure, result);
            result.Decisions = run.Schedule.Decisions.ToArray();
            if (failure == null) result.Verdict = ExplorerVerdict.Passed;
            else
            {
                result.Verdict = ExplorerVerdict.Failed;
                result.Failure = failure;
                result.FailureId = host.FailureId(failure);
                result.ArtifactPath = ExplorerArtifacts.WriteFailure(result, scenario);
            }
            return result;
        }

        /// <summary>Dimension points that are outside every connection mode's contract.</summary>
        internal static string DimensionRule(ExplorerConfiguration c)
        {
            if (c.Mode == ExplorerMode.Direct && c.Callback == ExplorerCallback.Peer)
                return "callback=peer needs mode=shared: a Direct connection owns its file exclusively, so a second Direct " +
                    "connection to the same file is outside the contract (Windows refuses it by file sharing; Unix does not " +
                    "enforce FileShare.Read, see docs/concurrency-explorer.md)";
            return null;
        }

        private Exception Finish(Exception failure, ExplorerResult result)
        {
            if (failure != null) File.WriteAllLines(Path.Combine(this.Directory, "waitgraph.txt"), ExplorerDriverEdges.Findings());
            var stopped = this.Schedule.Stop(failure != null);
            if (!stopped)
            {
                result.LiveWorker = true;
                return failure ?? new ExplorerFailure("EXPLORER_LIVE_WORKER", "an actor did not terminate; fixture retained");
            }
            LiteDatabase[] live;
            lock (_connections) live = _connections.Where(db => !_disposed.Contains(db)).Reverse().ToArray();
            foreach (var db in live) failure = Attempt(failure, "ExplorerCleanup", () => this.Dispose(db, "Dispose (cleanup)"));
            for (var i = _resources.Count - 1; i >= 0; i--) failure = Attempt(failure, "ExplorerCleanup" + i, _resources[i].Dispose);
            // Cleanup failures must not suppress the independent persisted-state check.
            var models = new[] { this.Model, _other }.Where(model => model != null).Concat(_files.Values).ToArray();
            failure = Attempt(failure, "ExplorerColdOracle", () =>
            {
                foreach (var model in models) model.VerifyCold(this.Host);
            });
            failure = Attempt(failure, "ExplorerQuiescent", () =>
            {
                foreach (var model in models) this.Host.Quiescent(model.Path, "scenario end");
            });
            this.Schedule.Event(failure == null ? "PASS cold-state-verified" : "FAIL retained " + this.Directory);
            this.Schedule.Event("flushing finalizers (a page buffer leaked or released twice by this run fails here, not in a later run)");
            this.Schedule.Dispose();
            FlushFinalizers();
            var leaked = _auditing ? TakeFinalizedInUse() : null;
            if (leaked != null && failure == null)
                failure = new ExplorerFailure("EXPLORER_PAGE_BUFFER_FINALIZED_IN_USE",
                    "page buffer(s) became garbage with share count(s) " + leaked + " by the end of this run (leaked or released twice)");
            return failure;
        }

        private static Exception Attempt(Exception failure, string key, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (failure == null) return error;
                failure.Data[key] = error.ToString();
            }
            return failure;
        }
    }
}
