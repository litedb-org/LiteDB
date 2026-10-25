using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Bounded forced schedules of explicit transaction handles, shared by xUnit and the
    /// isolated fuzz runner. Every schedule ends with an exact cold-reopen model check.
    /// </summary>
    /// <remarks>
    /// Ported from JKamsker/LiteDB#133 (34 indexed schedules) for litedb-org/LiteDB#3064,
    /// adapted to #3079's surface. Each connection mode has its own schedule table, so no
    /// index silently repeats another schedule (#133's indexes 18-23 repeated 12-17, and its
    /// close-active indexes repeated two distinct schedules eight times).
    /// Removed because #3079 deliberately excludes what they exercise:
    /// - Shared admission "cancellation" (2 per peer mode): needs
    ///   <c>BeginTransaction(TimeSpan, CancellationToken)</c>. Parameterless begin is kept.
    /// - <c>TransactionAdmission.Observe</c> stages: replaced by observable state, the local
    ///   handle queue's waiter count and the native turnstile.
    /// - <c>SessionLifetime.BeforeCloseDispatch</c>: close-active observes the facade's closing
    ///   flag and the handle's close wait instead. Close is unbounded: it waits for the call.
    /// - <c>DirectEngineLease</c>/<c>OperationLifetime</c> maintenance fencing: maintenance observes
    ///   <c>TransactionGate._waitingWriters</c>, the writer-priority queue of #3079.
    /// - Pooled Direct host peers: a Direct peer is a second facade over one caller-owned engine.
    /// - Holder-reuse and refreshed-wrapper expectations: no such objects exist in #3079.
    /// Added: a legacy close from a foreign thread (dev's lock semantics) and a handle
    /// callback reading while maintenance waits for that handle.
    /// </remarks>
    internal sealed partial class TransactionInterleavingExplorer
    {
        private sealed class Schedule
        {
            internal readonly string Name;
            internal readonly Action<TransactionInterleavingExplorer, int> Body;
            internal Schedule(string name, Action<TransactionInterleavingExplorer, int> body) { Name = name; Body = body; }
        }

        private static readonly Schedule[] Direct = Build(false), Shared = Build(true);
        internal static int ScheduleCount(bool shared) => (shared ? Shared : Direct).Length;
        internal static string ScheduleName(bool shared, int schedule) => (shared ? Shared : Direct)[schedule].Name;

        private static Schedule[] Build(bool shared)
        {
            var list = new List<Schedule>();
            void Add(string name, Action<TransactionInterleavingExplorer, int> body) => list.Add(new Schedule(name, body));
            for (var order = 0; order < 6; order++)
                foreach (var peer in new[] { false, true })
                {
                    var o = order; var p = peer;
                    Add("overlap-reader-transfer order=" + o + " peer=" + p, (run, seed) => run.OverlapAndReaderTransfer(o, p, seed));
                }
            if (!shared)
            {
                for (var variant = 0; variant < 6; variant++)
                    foreach (var peer in new[] { false, true })
                    {
                        var v = variant; var p = peer;
                        Add("independent-handles variant=" + v + " peer=" + p, (run, seed) => run.IndependentHandles(v, p, seed));
                    }
                foreach (var handle in new[] { false, true })
                {
                    var h = handle;
                    Add("maintenance-waits callback=" + (h ? "handle" : "ordinary"), (run, seed) => run.Lifecycle().Maintenance(h, seed));
                }
            }
            else
            {
                foreach (var legacy in new[] { false, true })
                    foreach (var closing in new[] { false, true })
                        foreach (var peer in new[] { false, true })
                        {
                            var l = legacy; var c = closing; var p = peer;
                            Add("shared-admission owner=" + (l ? "legacy" : "handle") + " waiter=" + (c ? "closing" : "admitted") + " peer=" + p,
                                (run, seed) => ExplorerAdmission.Run(run._schedule, run._model, run._resources, run._a, run._b, run._c, l, c, p, seed));
                        }
                for (var variant = 0; variant < 4; variant++)
                {
                    var v = variant;
                    Add("ordinary-callback variant=" + v, (run, seed) =>
                    {
                        if (v == 2) run._otherModel = new ExplorerDatabase(run._model.Connection.Filename + ".other", true, run._model.Connection.Password != null);
                        ExplorerOrdinaryCallbacks.Run(run._schedule, run._model, run._otherModel, run._resources, run._a, run._c, v);
                    });
                }
            }
            foreach (var waiting in new[] { false, true })
            {
                var w = waiting;
                Add("close-active stage=" + (w ? "close-waits-for-call" : "closing-published"), (run, seed) => run.Lifecycle().CloseActive(w, seed));
            }
            Add("legacy-close-foreign-thread", (run, seed) => run.Lifecycle().LegacyClose(seed));
            if (!shared)
                foreach (var reverse in new[] { false, true })
                {
                    var r = reverse;
                    Add("collection-lock-cycle reverse=" + r, (run, seed) =>
                        ExplorerCollectionCycle.Run(run._schedule, run._model, run._resources, run._a, run._b, r));
                }
            return list.ToArray();
        }

        private readonly ExplorerSchedule _schedule;
        private readonly ExplorerDatabase _model;
        private ExplorerDatabase _otherModel;
        private readonly List<IDisposable> _resources = new List<IDisposable>();
        private readonly bool _shared;
        private readonly ExplorerSchedule.Actor _a, _b, _c;

        private TransactionInterleavingExplorer(string file, bool shared, bool encrypted, int schedule, int seed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            _schedule = new ExplorerSchedule(file + ".history", "seed=" + seed + " schedule=" + schedule + " (" +
                ScheduleName(shared, schedule) + ") shared=" + shared + " encrypted=" + encrypted);
            _shared = shared;
            _model = new ExplorerDatabase(file, shared, encrypted);
            _a = _schedule.NewActor("A"); _b = _schedule.NewActor("B"); _c = _schedule.NewActor("C");
        }

        internal static void Run(string file, bool shared, bool encrypted, int schedule, int seed)
        {
            if (schedule < 0 || schedule >= ScheduleCount(shared)) throw new ArgumentOutOfRangeException(nameof(schedule));
            var run = new TransactionInterleavingExplorer(file, shared, encrypted, schedule, seed);
            Exception failure = null;
            try
            {
                (shared ? Shared : Direct)[schedule].Body(run, seed);
                run._schedule.CheckActors();
            }
            catch (Exception error)
            {
                failure = error; error.Data["ExplorerFixture"] = file;
                error.Data["ExplorerSchedule"] = schedule; error.Data["ExplorerSeed"] = seed;
                run._schedule.Event("FAIL " + error);
            }
            finally
            {
                var stopped = run._schedule.Stop();
                if (!stopped)
                {
                    if (failure == null) failure = new TimeoutException("A worker did not terminate; retained " + file);
                    failure.Data["ExplorerLiveWorker"] = true;
                }
                if (stopped)
                {
                    for (var i = run._resources.Count - 1; i >= 0; i--)
                    {
                        try { run._resources[i]?.Dispose(); }
                        catch (Exception error)
                        {
                            if (failure == null) failure = error;
                            else failure.Data["ExplorerCleanup" + i] = error;
                        }
                    }
                    // Cleanup failure must not suppress the independent persisted-state check.
                    run._schedule.Event("cold-verification-start");
                    try { run._model.VerifyCold(); run._otherModel?.VerifyCold(); }
                    catch (Exception error)
                    {
                        if (failure == null) failure = error;
                        else failure.Data["ExplorerColdOracle"] = error;
                    }
                    run._schedule.Event(failure == null ? "PASS cold-state-verified" : "FAIL retained " + file);
                    run._schedule.Dispose();
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private ExplorerLifecycle Lifecycle() => new ExplorerLifecycle(_schedule, _model, _resources, _a, _b, _c);
        private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
        private LiteDatabase Open() => Keep(_model.Open());

        /// <summary>Two facades of one database: Shared connections, or Direct facades over one engine.</summary>
        private void OpenPeers(bool peer, out LiteDatabase first, out LiteDatabase second)
        {
            if (!peer) { first = second = Open(); return; }
            if (_shared) { first = Open(); second = Open(); return; }
            var engine = Keep(_model.OpenEngine());
            first = Keep(ExplorerDatabase.Peer(engine));
            second = Keep(ExplorerDatabase.Peer(engine));
        }

        private static IEnumerable<BsonDocument> Input(ExplorerSchedule.Boundary boundary, int id, int value, Action callback = null)
        { callback?.Invoke(); boundary.Hit(); yield return ExplorerDatabase.Row(id, value); }
    }
}
