using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>Direct-mode sweep drivers: a raw <see cref="LiteEngine"/> with the case's prior state.</summary>
    internal static partial class TeardownDrivers
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private static IEnumerable<TeardownDriver> Direct()
        {
            yield return D("LiteEngine.Close", "open-work", "LiteEngine.Close() and its returned failure list", c =>
            {
                var engine = Engine(c, out _);
                c.Invoke(() => (IEnumerable<Exception>)engine.Close());
            }, Spilled);
            yield return D("LiteEngine.CloseOnError", "fatal", "LiteEngine.Close(Exception) with an I/O cause, and its returned list", c =>
            {
                var engine = Engine(c, out _);
                c.Primary = new IOException("teardown-sweep primary: the fatal cause");
                c.Invoke(() => (IEnumerable<Exception>)engine.Close(c.Primary));
            }, Spilled);
            yield return D("LiteEngine.CloseOnError", "invalid-datafile", "LiteEngine.Close(Exception) with INVALID_DATAFILE_STATE", c =>
            {
                var engine = Engine(c, out _);
                c.Primary = new LiteException(LiteException.INVALID_DATAFILE_STATE, "teardown-sweep primary: invalid datafile");
                c.ReopenWithAutoRebuild = true;
                c.Invoke(() => (IEnumerable<Exception>)engine.Close(c.Primary));
            });
            yield return D("LiteEngine.Dispose", "open-work", "LiteEngine.Dispose()", c =>
            {
                var engine = Engine(c, out _);
                c.Invoke(engine.Dispose);
            }, Spilled);
            yield return D("LiteEngine.Rebuild", "committed", "LiteEngine.Rebuild(options) closing the old engine", c =>
            {
                var engine = Engine(c, out _);
                c.Invoke(() => engine.Rebuild(new RebuildOptions { Password = c.Password }));
            }, () => TeardownPrior.Minimal());
            yield return D("EngineState.Stop", "write-failure", "an insert whose WAL write fails (the operation's own error)", c =>
            {
                var engine = Engine(c, out var db);
                var primary = new IOException("teardown-sweep primary: WAL write failed");
                var fired = 0;
                engine.SimulateDiskWriteFail = page => { if (Interlocked.Exchange(ref fired, 1) == 0) throw primary; };
                c.Primary = primary;
                c.Ledger.Abort("rows", 300, null);
                c.Invoke(() => db.GetCollection("rows").Insert(TeardownStates.Row(300)));
            });
            yield return D("TransactionMonitor.Dispose", "open-work", "TransactionMonitor.Dispose() with open transactions", c =>
            {
                var engine = Engine(c, out _);
                c.Invoke(() => engine.GetMonitor().Dispose());
            });
            yield return D("TransactionService.Dispose", "rollback", "Rollback() of the caller's explicit transaction", c =>
            {
                var engine = Engine(c, out var db);
                db.BeginTrans();
                for (var id = 400; id < 403; id++)
                {
                    db.GetCollection("rows").Insert(TeardownStates.Row(id));
                    c.Ledger.Abort("rows", id, null);
                }
                c.Invoke(() => db.Rollback());
            });
            yield return D("TransactionService.Dispose", "auto-commit", "an insert's automatic transaction release", c =>
            {
                var engine = Engine(c, out var db);
                // Committed before its release failed: outcome uncertain, so not ledgered.
                c.Invoke(() => db.GetCollection("rows").Insert(TeardownStates.Row(410)));
            });
            yield return D("DiskService.Dispose", "after-monitor", "DiskService.Dispose() after the monitor, as LiteEngine.Close orders it", c =>
            {
                var engine = Engine(c, out _);
                engine.GetMonitor().Dispose();
                var disk = Field<DiskService>(engine, "_disk");
                c.Invoke(disk.Dispose);
            });
            yield return D("SortDisk.Dispose", "after-spill", "SortDisk.Dispose() after the monitor, as LiteEngine.Close orders it", c =>
            {
                var engine = Engine(c, out _);
                engine.GetMonitor().Dispose();
                var sort = Field<SortDisk>(engine, "_sortDisk");
                c.Invoke(sort.Dispose);
            }, () => new TeardownPrior { PendingTransaction = false, OpenReader = false, SpilledSort = true }, prior => prior.SpilledSort = true);
            yield return D("SortService.Dispose", "spilled-reader", "disposing a reader whose sort spilled", c =>
            {
                Engine(c, out var db, spilled: false);
                var reader = TeardownStates.SpilledReader(db, c, fileScratch: true);
                c.Invoke(reader.Dispose);
            }, () => new TeardownPrior { SpilledSort = true }, prior => prior.SpilledSort = true);
            yield return D("BsonDataReader.Dispose", "partial", "disposing a partially read query reader", c =>
            {
                var engine = Engine(c, out _);
                var reader = engine.Query("many", new Query());
                reader.Read();
                c.Invoke(reader.Dispose);
            });
            yield return D("AesStream.Dispose", "data-file", "disposing the encrypted stream a file factory opens", c =>
            {
                var path = TeardownStates.Database(c);
                var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                var aes = new AesStream(c.Password, stream, allowRecovery: false);
                var page = new byte[Constants.PAGE_SIZE];
                aes.Position = 0;
                aes.Read(page, 0, page.Length);
                c.Invoke(aes.Dispose);
                c.Defer(stream.Dispose);
            }, () => new TeardownPrior { PendingTransaction = false, OpenReader = false, Encrypted = true }, prior => prior.Encrypted = true);
            yield return D("LiteDatabase.Dispose", "stream", "LiteDatabase.Dispose() of a stream database (checkpoint override)", c =>
            {
                var path = TeardownStates.Database(c);
                var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                c.Defer(stream.Dispose);
                var db = new LiteDatabase(stream);
                TeardownStates.RecentWrites(db, c);
                c.Disposed.Add(db);
                c.Invoke(db.Dispose);
            }, () => TeardownPrior.Minimal(), prior => prior.Encrypted = false); // the stream constructor takes no password
            yield return D("LiteDatabase.Dispose", "file", "LiteDatabase.Dispose() of a file database", c =>
            {
                TeardownStates.Database(c);
                var db = new LiteDatabase(new ConnectionString { Filename = c.DatabasePath, Password = c.Password });
                Prepare(db, c, fileScratch: true);
                c.Disposed.Add(db);
                c.Invoke(db.Dispose);
            });
            yield return D("RebuildService.DiscardReplacement", "duplicate-key", "a rebuild whose replacement build fails", c =>
            {
                var engine = Engine(c, out var db);
                // A binary collation keeps "Alpha" and "alpha" apart; the rebuild to IgnoreCase cannot.
                engine.Rebuild(new RebuildOptions { Collation = Collation.Binary, Password = c.Password });
                var names = db.GetCollection("names");
                names.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "Alpha" });
                names.Insert(new BsonDocument { ["_id"] = 2, ["name"] = "alpha" });
                names.EnsureIndex("name", "name", true);
                c.Ledger.Acknowledge("names", 1, new BsonDocument { ["_id"] = 1, ["name"] = "Alpha" });
                c.Ledger.Acknowledge("names", 2, new BsonDocument { ["_id"] = 2, ["name"] = "alpha" });
                c.Invoke(() => engine.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase"), Password = c.Password }));
                // The failed build's own error is the primary the cleanup failure is recorded on.
                if (c.Thrown is LiteException lite && lite.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) c.Primary = c.Thrown;
                foreach (var leftover in new[] { "sweep-temp.db", "sweep-temp-log.db" })
                    if (File.Exists(c.File(leftover)) && c.Scenario.Fired == null)
                        c.Violation("driver.replacement-left", leftover + " remains after a discarded rebuild without a reported failure");
            }, () => TeardownPrior.Minimal());
        }

        /// <summary>A raw engine on the case's database with the prior state built through a wrapping LiteDatabase.</summary>
        private static LiteEngine Engine(TeardownCase c, out LiteDatabase db, bool spilled = true)
        {
            TeardownStates.Database(c);
            var engine = new LiteEngine(new EngineSettings { Filename = c.DatabasePath, Password = c.Password });
            db = new LiteDatabase(engine, disposeOnClose: false);
            c.Disposed.Add(engine);
            Prepare(db, c, fileScratch: true, spilled: spilled);
            c.Defer(engine.Dispose);
            return engine;
        }

        private static void Prepare(ILiteDatabase db, TeardownCase c, bool fileScratch, bool spilled = true)
        {
            TeardownStates.RecentWrites(db, c);
            if (c.Prior.OpenReader) TeardownStates.OpenReader(db, c);
            if (c.Prior.SpilledSort && spilled) TeardownStates.SpilledReader(db, c, fileScratch);
            if (c.Prior.PendingTransaction) TeardownStates.PendingTransaction(db, c);
        }

        private static T Field<T>(object instance, string name) where T : class =>
            instance.GetType().GetField(name, Private)?.GetValue(instance) as T
            ?? throw new InvalidOperationException($"{instance.GetType().Name}.{name} no longer exists; update the teardown driver.");

        private static TeardownPrior Spilled() => new TeardownPrior { SpilledSort = true };

        private static TeardownDriver D(string path, string variant, string entry, Action<TeardownCase> drive,
            Func<TeardownPrior> defaults = null, Action<TeardownPrior> require = null) =>
            new TeardownDriver(path, variant, TeardownMode.Direct, entry, drive, defaults, require: require);
    }
}
