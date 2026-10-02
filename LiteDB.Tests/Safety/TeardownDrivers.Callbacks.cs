using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// Shared drivers whose teardown reaches user code. The connection runs over caller-owned data and
    /// log streams; the data stream's first write during the teardown (a close checkpoint) calls back
    /// into the same connection, a getter or Dispose, as a user's stream wrapper may
    /// (docs/rules/storage-ownership.md: user callbacks run inside the ownership of their call).
    /// Invariant (M1 Ownership): a protected core that is still tearing down keeps its native exclusion,
    /// so nothing the callback does may release the writer mutex before the teardown returns. A
    /// foreign thread probes the real mutex before and after the nested call.
    /// Added after the row-17 proof (023c2b4ba) did not fire, with knowledge of the #3077 fix and its
    /// test (tuned-after-fix; see /tmp/safety-net/proofs/row17-teardown-sweep.json).
    /// </summary>
    internal static partial class TeardownDrivers
    {
        private enum CallbackRoute { RetainingReader, PinHolder, LastLeasedReader, ConnectionWithEngine, ConnectionCheckpoint }

        private static IEnumerable<TeardownDriver> Callbacks()
        {
            var routes = new[]
            {
                (CallbackRoute.RetainingReader, "SharedDataReader.Dispose", "disposing a write-retaining reader closes its core"),
                (CallbackRoute.PinHolder, "SharedMutexPin.Hold", "disposing the pinned thread's reader ends the pin"),
                (CallbackRoute.LastLeasedReader, "SharedEngine.CheckpointAfterLastReader", "disposing the last leased reader checkpoints"),
                (CallbackRoute.ConnectionWithEngine, "SharedEngine.Dispose", "Dispose() closing a retained core"),
                (CallbackRoute.ConnectionCheckpoint, "SharedEngine.CheckpointOnDispose", "Dispose() checkpointing an idle WAL"),
            };
            foreach (var (route, path, entry) in routes)
                foreach (var nested in new[] { "getter", "dispose" })
                {
                    var driver = S(path, "self-callback-" + nested, entry + "; a close write calls " + nested + " on the same connection",
                        c => SelfCallback(c, route, nested == "dispose"), () => TeardownPrior.Minimal());
                    driver.BaselineOnlyByDefault = true;
                    yield return driver;
                }
        }

        private static void SelfCallback(TeardownCase c, CallbackRoute route, bool nestedDispose)
        {
            var path = TeardownStates.Database(c);
            var data = new CallbackStream(path);
            var log = new CallbackStream(FileHelper.GetLogFile(path));
            var engine = new SharedEngine(new EngineSettings
            {
                Filename = path, Password = c.Password, DataStream = data, LogStream = log, ReadTransform = (_, value) => value
            });
            // The pin must end when its reader is disposed, not on an idle timer.
            engine.PinIdleLimit = TimeSpan.FromMinutes(10);
            engine.PinHoldLimit = TimeSpan.FromMinutes(10);
            var db = new LiteDatabase(engine);
            c.Disposed.Add(db);
            c.Defer(db.Dispose);
            c.Defer(() => { data.Dispose(); log.Dispose(); }); // caller-owned: closed after the connection
            TeardownStates.RecentWrites(db, c);

            var called = 0;
            var releasedInTeardown = false;
            data.Arm(null);
            Action callback = () =>
            {
                called++;
                var before = NativeExcluded(engine);
                try
                {
                    if (nestedDispose) engine.Dispose();
                    else engine.Pragma("USER_VERSION");
                }
                catch (Exception) { /* a refusal is a valid answer; the ownership is what is judged */ }
                releasedInTeardown = before && !NativeExcluded(engine);
            };

            switch (route)
            {
                case CallbackRoute.RetainingReader:
                case CallbackRoute.ConnectionWithEngine:
                {
                    var reader = engine.Query("rows", new Query { ForUpdate = true });
                    Big(db, c);
                    data.Arm(callback);
                    if (route == CallbackRoute.ConnectionWithEngine) c.Invoke(db.Dispose);
                    else c.Invoke(reader.Dispose);
                    reader.Dispose();
                    break;
                }
                case CallbackRoute.PinHolder:
                {
                    var reader = engine.Query("many", new Query());
                    reader.Read();
                    Big(db, c);
                    if (engine.Pin == null) c.Violation("driver.no-pin", "the reader thread's write did not start a pin");
                    data.Arm(callback);
                    c.Invoke(reader.Dispose);
                    break;
                }
                case CallbackRoute.LastLeasedReader:
                {
                    var reader = engine.Query("many", new Query());
                    reader.Read();
                    using (var peer = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Shared, Password = c.Password }))
                    {
                        peer.GetCollection("rows").Upsert(BigRow(1));
                        c.Ledger.Acknowledge("rows", 1, BigRow(1));
                    }
                    data.Arm(callback);
                    c.Invoke(reader.Dispose);
                    break;
                }
                case CallbackRoute.ConnectionCheckpoint:
                    data.Arm(callback);
                    c.Invoke(db.Dispose);
                    break;
            }
            // An armed fault may stop the teardown before its first write; without one the write must happen.
            if (called == 0 && c.Scenario.Fired == null) c.Violation("driver.no-callback", "no write reached the caller stream during the teardown");
            if (releasedInTeardown)
                c.Violation("ownership.released-in-teardown-callback",
                    "a same-connection " + (nestedDispose ? "Dispose" : "getter") + " from a teardown callback released the native writer mutex " +
                    "while the core was still tearing down (a foreign thread acquired it before the callback returned)");
        }

        /// <summary>Rows large enough that the close checkpoint writes to the data stream.</summary>
        private static void Big(LiteDatabase db, TeardownCase c)
        {
            var rows = Enumerable.Range(700, 60).Select(BigRow).ToArray();
            db.GetCollection("rows").Insert(rows);
            foreach (var row in rows) c.Ledger.Acknowledge("rows", row["_id"].AsInt32, row);
        }

        private static BsonDocument BigRow(int id)
        {
            var row = TeardownStates.Row(id);
            row["payload"] = new string('p', 4000);
            return row;
        }

        /// <summary>Whether a fresh foreign thread fails to take the connection's native writer mutex right now.</summary>
        private static bool NativeExcluded(SharedEngine engine)
        {
            var acquired = false;
            var probe = new Thread(() =>
            {
                try { acquired = engine.MutexOwner.Mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                finally { if (acquired) engine.MutexOwner.Mutex.ReleaseMutex(); }
            }) { IsBackground = true, Name = "teardown-sweep mutex probe" };
            probe.Start();
            if (!probe.Join(TeardownParticipant.Bound)) throw new TimeoutException("The native mutex probe did not return.");
            return !acquired;
        }

        /// <summary>A caller-owned file stream whose next write, once armed, first runs a callback.</summary>
        private sealed class CallbackStream : FileStream
        {
            private Action _onWrite;

            public CallbackStream(string path)
                : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)
            {
            }

            public void Arm(Action callback) => Volatile.Write(ref _onWrite, callback);

            public override void Write(byte[] array, int offset, int count)
            {
                Interlocked.Exchange(ref _onWrite, null)?.Invoke();
                base.Write(array, offset, count);
            }
#if NETCOREAPP
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Interlocked.Exchange(ref _onWrite, null)?.Invoke();
                base.Write(buffer);
            }
#endif
        }
    }
}
