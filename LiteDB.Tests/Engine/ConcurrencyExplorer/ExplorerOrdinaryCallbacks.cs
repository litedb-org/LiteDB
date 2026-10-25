using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Shared: bounded diagnostic of ordinary (non-handle) callback ownership cycles next to
    /// handles. A detected wait is a failure. Ported unchanged from JKamsker/LiteDB#133.
    /// </summary>
    internal static class ExplorerOrdinaryCallbacks
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object value, string name) => value.GetType().GetField(name, Fields)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing observation field " + name);

        internal static void Run(ExplorerSchedule schedule, ExplorerDatabase model, ExplorerDatabase other,
            List<IDisposable> resources, ExplorerSchedule.Actor actor, ExplorerSchedule.Actor cleanup, int variant)
        {
            var settings = new EngineSettings { Filename = model.Connection.Filename, Password = model.Connection.Password,
                ReadTransform = (_, value) => value };
            var shared = new SharedEngine(settings);
            var db = new LiteDatabase(shared);
            resources.Add(db);
            var destination = variant == 2 ? other : model;
            var peer = variant == 0 ? db : destination.Open();
            if (peer != db) resources.Add(peer);
            var peerEngine = (SharedEngine)Field(peer, "_engine");
            IBsonDataReader anchor = null;
            if (variant != 3)
            {
                actor.Run("ordinary leased anchor on future writer thread", () =>
                {
                    anchor = shared.Query("rows", new Query());
                    ExplorerDatabase.Require(anchor.Read(), "ordinary callback anchor empty");
                    ExplorerDatabase.Require(((System.Collections.IDictionary)Field(shared, "_localReaders")).Count > 0, "anchor did not establish leased path");
                });
                resources.Add(anchor);
            }
            var entered = schedule.NewBoundary("ordinary input callback executing with writer ownership");
            var finished = new ManualResetEventSlim();
            var emergency = 0;
            var refused = false;
            IEnumerable<BsonDocument> Input()
            {
                yield return ExplorerDatabase.Row(2, 20);
                if (variant != 3)
                {
                    var pin = Field(shared, "_pin");
                    ExplorerDatabase.Require((int)Field(pin, "_operations") > 0 && (int)Field(pin, "_holds") == 0,
                        "callback did not hold expected pin operation");
                }
                entered.Observe();
                try { peer.GetCollection("other").Insert(ExplorerDatabase.Row(2, 50)); }
                catch (InvalidOperationException error) when ((variant == 1 || variant == 3) &&
                    error.GetType() == typeof(InvalidOperationException) && error.Message == ExplorerDatabase.OrdinaryCallbackRefusal)
                { refused = true; }
                catch (OperationCanceledException error) when (Volatile.Read(ref emergency) != 0 && error.CancellationToken.IsCancellationRequested) { }
                finally { finished.Set(); }
                yield return ExplorerDatabase.Row(3, 30);
            }
            var work = actor.Invoke("ordinary-input-callback", () => shared.Insert("rows", Input(), BsonAutoId.Int32));
            entered.Wait();
            var sameFilePeer = variant == 1 || variant == 3;
            schedule.Until(() => finished.IsSet || (sameFilePeer && (int)Field(peerEngine, "_mutexWaiters") > 0),
                "callback must complete/refuse or expose native ownership wait");
            var cycle = !finished.IsSet;
            if (cycle)
            {
                // Release only the waiting facade to bound the diagnostic. This is NOT a
                // successful deadlock prevention result; the exact reached cycle fails below.
                schedule.Event("DEFECT ordinary callback waits native ownership retained by its caller");
                Interlocked.Exchange(ref emergency, 1);
                cleanup.Run("cancel-known-deadlock-waiter", peer.Dispose);
            }
            actor.Complete(work);
            anchor?.Dispose();
            model.Acknowledge("rows", 2, 20); model.Acknowledge("rows", 3, 30);
            if (variant == 0 || variant == 2) destination.Acknowledge("other", 2, 50);
            ExplorerDatabase.Require(!cycle, "ordinary same-file callback entered native self-wait (candidate merge blocker)");
            if (variant == 1 || variant == 3) ExplorerDatabase.Require(refused, "same-file callback did not refuse ownership dependency");
        }
    }
}
