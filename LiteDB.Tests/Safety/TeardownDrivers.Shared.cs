using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Safety
{
    /// <summary>Shared-mode sweep drivers: a Shared <see cref="LiteDatabase"/> with the case's prior state.</summary>
    internal static partial class TeardownDrivers
    {
        private static IEnumerable<TeardownDriver> Shared()
        {
            yield return S("SharedEngine.Dispose", "open-work", "Dispose() with a foreign transaction, reader and peer open", c =>
            {
                var db = Connection(c);
                c.Invoke(db.Dispose);
            }, () => new TeardownPrior { Peer = true });
            yield return S("SharedEngine.Dispose", "pin", "Dispose() forcing the pin of a thread iterating a leased reader", c =>
            {
                var db = Connection(c);
                Pin(c, db, disposeReaderInWindow: false);
                c.Invoke(db.Dispose);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedEngine.Dispose", "from-own-operation", "Dispose() called back from the connection's own operation on its pinned thread", c =>
            {
                var db = Connection(c);
                DisposeFromOwnOperation(c, db);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedEngine.ClosePin", "dispose", "Dispose(), which rethrows the pin holder's close failure", c =>
            {
                var db = Connection(c);
                Pin(c, db, disposeReaderInWindow: false);
                c.Invoke(db.Dispose);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedMutexPin.Hold", "reader-end", "the pinned thread disposing its last leased reader", c =>
            {
                var db = Connection(c);
                Pin(c, db, disposeReaderInWindow: true);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedEngine.CheckpointOnDispose", "idle-wal", "Dispose() of an idle connection whose WAL holds commits", c =>
            {
                var db = Connection(c);
                c.Invoke(db.Dispose);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedEngine.CheckpointAfterLastReader", "last-reader", "disposing the last leased reader", c =>
            {
                var db = Connection(c);
                var reader = db.GetCollection("many").Query().ToEnumerable().GetEnumerator();
                reader.MoveNext();
                c.Invoke(reader.Dispose);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedDataReader.Dispose", "leased", "disposing a leased reader", c =>
            {
                var db = Connection(c);
                var reader = db.GetCollection("many").Query().ToEnumerable().GetEnumerator();
                reader.MoveNext();
                c.Invoke(reader.Dispose);
            }, () => TeardownPrior.Minimal());
            yield return S("SharedMutexOwner.Exit", "scoped-write", "a scoped write (EnsureIndex) ending its own ownership", c =>
            {
                var db = Connection(c);
                // EnsureIndex, UpdateMany, DeleteMany, ... own the OS mutex on the calling thread (scoped);
                // Insert goes through the holder. An index changes no document, so the ledger stays exact.
                c.Invoke(() => db.GetCollection("rows").EnsureIndex("value"));
            }, () => TeardownPrior.Minimal());
            yield return S("SharedMutexOwner.ReleaseAll", "foreign-transaction", "Dispose() ending another thread's open transaction", c =>
            {
                var db = Connection(c);
                c.Invoke(db.Dispose);
            }, () => new TeardownPrior { OpenReader = false }, prior => prior.PendingTransaction = true); // without one, nothing is released
            yield return S("SharedMutexOwner.ReleaseExitedOwner", "exited-owner", "the holder's cleanup after a transaction owner exited", c =>
                ExitedOwner(c), () => TeardownPrior.Minimal());
            yield return S("SharedEngine.OnOwnerExited", "exited-owner", "the holder's cleanup after a transaction owner exited", c =>
                ExitedOwner(c), () => TeardownPrior.Minimal());
            yield return S("LiteDatabase.Dispose", "shared", "LiteDatabase.Dispose() of a Shared connection", c =>
            {
                var db = Connection(c);
                c.Invoke(db.Dispose);
            });
#if NET8_0_OR_GREATER
            yield return S("SharedEngine.OpenEngine", "publication-failure", "an insert whose new core fails publication", c =>
            {
                var db = Connection(c);
                var engine = (SharedEngine)ConnectionCleanProbe.EngineOf(db);
                var primary = new IOException("teardown-sweep primary: core publication failed");
                var fired = 0;
                engine.CoordinationStage = stage =>
                {
                    if (stage == "opened" && Interlocked.Exchange(ref fired, 1) == 0) throw primary;
                };
                c.Primary = primary;
                c.Ledger.Abort("rows", 510, null);
                c.Invoke(() => db.GetCollection("rows").Insert(TeardownStates.Row(510)));
            }, () => TeardownPrior.Minimal());
#else
            yield return new TeardownDriver("SharedEngine.OpenEngine", "publication-failure", TeardownMode.Shared,
                "an insert whose new core fails publication", c => { }, notApplicable:
                "the publication step whose failure this path cleans up (mapped coordination) exists only on .NET 8+");
#endif
        }

        /// <summary>A Shared connection with recent writes and the case's participants; disposed at scenario end.</summary>
        private static LiteDatabase Connection(TeardownCase c)
        {
            TeardownStates.Database(c);
            var connection = new ConnectionString { Filename = c.DatabasePath, Connection = ConnectionType.Shared, Password = c.Password };
            var db = new LiteDatabase(connection);
            c.Disposed.Add(db);
            c.Defer(db.Dispose);
            TeardownStates.RecentWrites(db, c);
            if (c.Prior.Peer)
            {
                var peer = new LiteDatabase(connection);
                peer.GetCollection("rows").Insert(TeardownStates.Row(Peer));
                c.Ledger.Acknowledge("rows", Peer, TeardownStates.Row(Peer));
                c.Defer(peer.Dispose);
            }
            if (c.Prior.OpenReader) TeardownStates.OpenReader(db, c);
            if (c.Prior.SpilledSort) TeardownStates.SpilledReader(db, c, fileScratch: false);
            // Last: an explicit transaction holds the writer mutex until the teardown ends it.
            if (c.Prior.PendingTransaction) TeardownStates.PendingTransaction(db, c);
            return db;
        }

        private const int Peer = 600;

        /// <summary>
        /// A thread iterating a leased reader writes, which pins the connection's mutex on a holder
        /// thread. With <paramref name="disposeReaderInWindow"/> the same thread then disposes the
        /// reader as the entry, ending the pin; otherwise it stays parked until the scenario ends.
        /// </summary>
        private static void Pin(TeardownCase c, LiteDatabase db, bool disposeReaderInWindow)
        {
            IEnumerator<BsonDocument> reader = null;
            var pinned = TeardownParticipant.Start(c, "pinned-reader", () =>
            {
                reader = db.GetCollection("many").Query().ToEnumerable().GetEnumerator();
                reader.MoveNext();
                db.GetCollection("rows").Insert(TeardownStates.Row(520));
                c.Ledger.Acknowledge("rows", 520, TeardownStates.Row(520));
                if (disposeReaderInWindow) c.Invoke(reader.Dispose);
            }, () => reader.Dispose());
            var engine = (SharedEngine)ConnectionCleanProbe.EngineOf(db);
            if (!disposeReaderInWindow && engine.Pin == null) c.Violation("driver.no-pin", "the reader thread's write did not start a pin");
        }

        /// <summary>
        /// A thread iterating a leased reader writes (which pins the mutex to it), then runs a bulk insert
        /// whose lazy input disposes the same connection: Dispose runs inside an operation of the pinned
        /// thread, which the pin's holder cannot wait for. The insert's outcome after that is not ledgered.
        /// </summary>
        private static void DisposeFromOwnOperation(TeardownCase c, LiteDatabase db)
        {
            IEnumerator<BsonDocument> reader = null;
            IEnumerable<BsonDocument> Input()
            {
                yield return TeardownStates.Row(541);
                c.Invoke(db.Dispose);
                yield return TeardownStates.Row(542);
            }
            TeardownParticipant.Start(c, "pinned-callback", () =>
            {
                reader = db.GetCollection("many").Query().ToEnumerable().GetEnumerator();
                reader.MoveNext();
                db.GetCollection("rows").Insert(TeardownStates.Row(540));
                c.Ledger.Acknowledge("rows", 540, TeardownStates.Row(540));
                try { db.GetCollection("rows").InsertBulk(Input()); }
                catch (Exception) { /* refused or ended by its own Dispose: the oracles judge the teardown */ }
            }, () => reader.Dispose());
            if (!c.Invoked) c.Violation("driver.no-callback", "the bulk insert never enumerated its input");
        }

        /// <summary>A thread begins a transaction and exits; the entry waits for the holder's exited-owner cleanup.</summary>
        private static void ExitedOwner(TeardownCase c)
        {
            var db = Connection(c);
            var engine = (SharedEngine)ConnectionCleanProbe.EngineOf(db);
            c.Ledger.Abort("rows", 530, null);
            c.Invoke(() =>
            {
                var owner = TeardownParticipant.Abandon(c, "exiting-owner", () =>
                {
                    db.BeginTrans();
                    db.GetCollection("rows").Insert(TeardownStates.Row(530));
                });
                if (!owner.Thread.Join(TeardownParticipant.Bound)) throw new TimeoutException("The exiting owner did not exit.");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (engine.MutexOwner.IsHeld && waited.Elapsed < TeardownParticipant.Bound) Thread.Sleep(5);
                engine.MutexOwner.WaitForRelease();
            });
            // The next call reports the exited owner; the connection is then disposed normally.
            c.Defer(() => { try { db.GetCollection("rows").Count(); } catch (LiteException) { } });
        }

        private static TeardownDriver S(string path, string variant, string entry, Action<TeardownCase> drive, Func<TeardownPrior> defaults = null,
            Action<TeardownPrior> require = null) =>
            new TeardownDriver(path, variant, TeardownMode.Shared, entry, drive, defaults, require: require);
    }
}
