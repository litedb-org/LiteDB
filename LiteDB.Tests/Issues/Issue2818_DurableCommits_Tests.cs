using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2818_DurableCommits_Tests
    {
        /// <summary>
        /// Counts device syncs and OS-cache flushes, and records the order of syncs across files.
        /// </summary>
        private sealed class CountingFile : FileStream
        {
            private readonly string _name;
            private readonly List<string> _syncOrder;

            public int DurableFlushes { get; private set; }
            public int PlainFlushes { get; private set; }
            public Exception DurableFailure { get; set; }

            public CountingFile(string path, string name, List<string> syncOrder)
                : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
            {
                _name = name;
                _syncOrder = syncOrder;
            }

            public override void Flush(bool flushToDisk)
            {
                base.Flush(false);

                if (flushToDisk == false)
                {
                    PlainFlushes++;
                    return;
                }

                DurableFlushes++;
                _syncOrder.Add(_name);
                if (DurableFailure != null) throw DurableFailure;
                base.Flush(true);
            }
        }

        private sealed class Storage : IDisposable
        {
            private readonly TempFile _dataFile = new TempFile();
            private readonly TempFile _logFile = new TempFile();

            public List<string> SyncOrder { get; } = new List<string>();
            public CountingFile Data { get; }
            public CountingFile Log { get; }

            public Storage()
            {
                Data = new CountingFile(_dataFile.Filename, "data", SyncOrder);
                Log = new CountingFile(_logFile.Filename, "log", SyncOrder);
            }

            public LiteEngine Open(bool durableCommits, string password)
            {
                return new LiteEngine(new EngineSettings
                {
                    DataStream = Data, LogStream = Log, Password = password, DurableCommits = durableCommits
                });
            }

            public void Dispose()
            {
                Data.Dispose();
                Log.Dispose();
                _dataFile.Dispose();
                _logFile.Dispose();
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Default_settings_sync_the_log_once_per_confirmed_transaction(string password)
        {
            new EngineSettings().DurableCommits.Should().BeTrue();
            new ConnectionString().DurableCommits.Should().BeTrue();

            using var storage = new Storage();
            using var engine = storage.Open(durableCommits: true, password);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = WarmUp(db);
            var syncsBefore = storage.Log.DurableFlushes;

            CommitFourTransactions(db, rows);

            storage.Log.DurableFlushes.Should().Be(syncsBefore + 4);
            DurableLogFlush(db).Should().BeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Opted_out_commits_reach_the_operating_system_without_a_device_sync(string password)
        {
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: false, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                var syncsBefore = storage.Log.DurableFlushes;
                var plainBefore = storage.Log.PlainFlushes;

                CommitFourTransactions(db, rows);

                storage.Log.DurableFlushes.Should().Be(syncsBefore, "an opted-out commit must never ask for a device sync");
                storage.Log.PlainFlushes.Should().BeGreaterThanOrEqualTo(plainBefore + 4,
                    "every commit still hands its pages to the operating system");
                DurableLogFlush(db).Should().BeFalse("the weaker guarantee must be discoverable");
                rows.Count().Should().Be(6);
            }

            using (var reopenedEngine = storage.Open(durableCommits: true, password))
            using (var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false))
            {
                reopened.GetCollection("rows").Count().Should().Be(6);
                DurableLogFlush(reopened).Should().BeTrue("the setting is per open, never stored in the file");
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Opted_out_checkpoint_syncs_the_log_and_then_the_data_file(string password)
        {
            using var storage = new Storage();
            using var engine = storage.Open(durableCommits: false, password);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = WarmUp(db);
            CommitFourTransactions(db, rows);
            storage.SyncOrder.Clear();

            db.Checkpoint();

            // Data pages are overwritten in place: the log that can redo them must be on the device first.
            storage.SyncOrder.Should().Equal("log", "log", "data", "data"); // Sync padding, seal redo, then publish data and salt.
        }

        // Plain files only: opening an encrypted WAL stream syncs its preamble, which
        // such storage already rejected before #2818.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Checkpoint_tolerates_log_storage_that_cannot_sync(bool durableCommits)
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                CommitFourTransactions(db, rows);
                storage.Log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");

                db.Checkpoint();
                rows.Insert(new BsonDocument { ["_id"] = 100 });
                db.Checkpoint();

                rows.Count().Should().Be(7);
                DurableLogFlush(db).Should().BeFalse("the weaker guarantee must be discoverable");
            }

            // The storage still cannot sync: reopening recovers and keeps writing (#2242).
            using (var engine = storage.Open(durableCommits: true, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = db.GetCollection("rows");
                rows.Count().Should().Be(7);
                rows.Insert(new BsonDocument { ["_id"] = 101 });
                db.Checkpoint();
                DurableLogFlush(db).Should().BeFalse();
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("rows").Count().Should().Be(8);
            DurableLogFlush(reopened).Should().BeTrue("storage that syncs again regains durable commits");
        }

        [Fact]
        public void Automatic_checkpoint_tolerates_log_storage_that_stops_syncing()
        {
            const string password = null;
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits: true, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                db.CheckpointSize = 1;
                db.Checkpoint();
                storage.Log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");

                // Each commit reaches the automatic checkpoint limit.
                for (var i = 1; i <= 3; i++) rows.Insert(new BsonDocument { ["_id"] = i });

                rows.Count().Should().Be(5);
            }

            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("rows").Count().Should().Be(5);
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void Checkpoint_stops_before_overwriting_data_when_the_log_sync_fails(bool durableCommits, string password)
        {
            using var storage = new Storage();

            using (var engine = storage.Open(durableCommits, password))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = WarmUp(db);
                CommitFourTransactions(db, rows);
                var before = ReadShared(storage.Data.Name);
                // A failed sync, unlike an unsupported one, leaves the redo state unknown.
                storage.Log.DurableFailure = new IOException("The request could not be performed because of an I/O device error.");

                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().Which.Should().BeSameAs(storage.Log.DurableFailure);
                ReadShared(storage.Data.Name).Should().Equal(before);
                Action write = () => rows.Insert(new BsonDocument { ["_id"] = 100 });
                write.Should().Throw<Exception>().WithMessage("*Dispose and reopen*");
            }

            storage.Log.DurableFailure = null;
            using var reopenedEngine = storage.Open(durableCommits: true, password);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("rows").Count().Should().Be(6);
            reopened.Checkpoint();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Opted_out_commits_are_recovered_from_a_copy_taken_while_the_engine_is_open(string password)
        {
            using var original = new TempFile();
            using var copy = new TempFile();
            var connection = new ConnectionString { Filename = original.Filename, Password = password, DurableCommits = false };

            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    db.CheckpointSize = 0;
                    var rows = db.GetCollection("rows");
                    for (var i = 1; i <= 50; i++) rows.Insert(new BsonDocument { ["_id"] = i, ["value"] = "inserted" });
                    for (var i = 1; i <= 50; i += 2) rows.Update(new BsonDocument { ["_id"] = i, ["value"] = "updated" });
                    rows.Delete(50).Should().BeTrue();

                    // What a killed process leaves behind: whatever the operating system was handed.
                    File.WriteAllBytes(copy.Filename, ReadShared(original.Filename));
                    File.WriteAllBytes(LogOf(copy), ReadShared(LogOf(original)));
                }

                using (var recovered = new LiteDatabase(new ConnectionString { Filename = copy.Filename, Password = password }))
                {
                    var rows = recovered.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32).ToList();

                    rows.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 49));
                    rows.Where(x => x["_id"].AsInt32 % 2 == 1).Should().OnlyContain(x => x["value"].AsString == "updated");
                    rows.Where(x => x["_id"].AsInt32 % 2 == 0).Should().OnlyContain(x => x["value"].AsString == "inserted");
                }
            }
            finally
            {
                File.Delete(LogOf(original));
                File.Delete(LogOf(copy));
            }
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void The_same_file_opens_with_either_setting_and_reports_the_one_in_use(ConnectionType type)
        {
            using var file = new TempFile();
            var expected = 0;

            try
            {
                foreach (var durable in new[] { false, true, false, true })
                {
                    var connection = new ConnectionString { Filename = file.Filename, Connection = type, DurableCommits = durable };

                    using (var db = new LiteDatabase(connection))
                    {
                        var rows = db.GetCollection("rows");

                        rows.Count().Should().Be(expected);
                        rows.Insert(new BsonDocument { ["_id"] = ++expected });
                        rows.Insert(new BsonDocument { ["_id"] = ++expected });
                        DurableLogFlush(db).Should().Be(durable);
                    }
                }
            }
            finally
            {
                File.Delete(LogOf(file));
            }
        }

        [Theory]
        [InlineData(":memory:")]
        [InlineData(":temp:")]
        public void Opting_out_is_harmless_for_storage_that_is_not_a_file(string filename)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, DurableCommits = false });
            var rows = db.GetCollection("rows");

            rows.Insert(new BsonDocument { ["_id"] = 1 });
            db.Checkpoint();

            rows.Count().Should().Be(1);
        }

        [Theory]
        [InlineData("filename=demo.db;durable commits=false")]
        [InlineData("Filename=demo.db;Durable Commits=False")]
        [InlineData("durable commits=false")]
        [InlineData("DURABLE COMMITS = FALSE")]
        public void Connection_string_key_is_parsed_and_reaches_the_engine_settings(string text)
        {
            var parsed = new ConnectionString(text) { Filename = ":memory:" };
            EngineSettings captured = null;

            parsed.DurableCommits.Should().BeFalse();
            using (parsed.CreateEngine(settings => captured = settings))
            {
                captured.DurableCommits.Should().BeFalse();
            }
        }

        [Fact]
        public void Connection_string_round_trips_the_switch_and_omits_the_default()
        {
            var off = new ConnectionString { Filename = "demo.db", DurableCommits = false };

            off.ToString().Should().Be("Filename=\"demo.db\";Durable Commits=False");
            new ConnectionString(off.ToString()).DurableCommits.Should().BeFalse();
            new ConnectionString("filename=demo.db;durable commits=true").DurableCommits.Should().BeTrue();
            new ConnectionString("filename=demo.db").DurableCommits.Should().BeTrue();
            new ConnectionString { Filename = "demo.db" }.ToString().Should().Be("demo.db");
        }

        private static ILiteCollection<BsonDocument> WarmUp(LiteDatabase db)
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");

            // The second insert reads the log, so the pooled log reader exists before counting starts:
            // opening an encrypted stream issues its own durable flush.
            rows.Insert(new BsonDocument { ["_id"] = -1 });
            rows.Insert(new BsonDocument { ["_id"] = 0 });

            return rows;
        }

        private static void CommitFourTransactions(LiteDatabase db, ILiteCollection<BsonDocument> rows)
        {
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            rows.Update(new BsonDocument { ["_id"] = 2, ["value"] = "updated" });
            db.BeginTrans();
            rows.Insert(new BsonDocument { ["_id"] = 3 });
            rows.Insert(new BsonDocument { ["_id"] = 4 });
            db.Commit();
        }

        private static bool DurableLogFlush(LiteDatabase db)
        {
            return db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
        }

        // The engine still holds both files open.
        private static byte[] ReadShared(string filename)
        {
            using var input = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        private static string LogOf(TempFile file)
        {
            return Path.Combine(Path.GetDirectoryName(file.Filename),
                Path.GetFileNameWithoutExtension(file.Filename) + "-log" + Path.GetExtension(file.Filename));
        }
    }
}
