using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2545_Tests
    {
        [Fact]
        public void Matching_WAL_replays_before_all_collections_are_dropped()
        {
            var scenario = CreateScenario();

            var persisted = DropAndReplace(
                scenario.CurrentData,
                scenario.LateLog,
                Enumerable.Range(0, 4).Select(i => "base" + i)
                    .Concat(Enumerable.Range(0, 4).Select(i => "mid" + i))
                    .Concat(Enumerable.Range(0, 4).Select(i => "late" + i))
                    .ToArray());

            AssertReplacement(persisted.Data, persisted.Log);
        }

        [Fact]
        public void Restored_data_file_cannot_be_corrupted_by_a_newer_WAL_during_drop()
        {
            var scenario = CreateScenario();
            using var data = CopyIntoExpandableStream(scenario.OldData);
            using var log = CopyIntoExpandableStream(scenario.LateLog);
            LiteDatabase db = null;

            var openError = Record.Exception(() => db = Open(data, log));

            if (openError != null)
            {
                openError.Should().BeOfType<LiteException>(
                    "an incompatible WAL should be rejected as a database error before it changes the restored data file");
                openError.Message.Should().NotContain("{0}");
                openError.Message.Should().NotContain("already contains an open transaction");
                data.ToArray().Should().Equal(scenario.OldData);
                AssertOldBackup(scenario.OldData);
                return;
            }

            using (db)
            {
                var names = db.GetCollectionNames().OrderBy(x => x).ToArray();
                names.Should().Equal(new[] { "base0", "base1", "base2", "base3" },
                    "a WAL from a later data-file generation must not be replayed onto a restored backup");

                foreach (var name in names)
                {
                    var collection = db.GetCollection(name);
                    collection.Count().Should().Be(500);
                    collection.FindById(1)["tag"].AsString.Should().Be(name + ":1");
                    db.DropCollection(name).Should().BeTrue();
                }

                InsertReplacement(db);
            }

            AssertReplacement(data.ToArray(), log.ToArray());
        }

        [Fact]
        public void Failed_drop_cannot_leak_its_transaction_or_an_unformatted_message()
        {
            var scenario = CreateScenario();
            using var data = CopyIntoExpandableStream(scenario.OldData);
            using var log = CopyIntoExpandableStream(scenario.LateLog);
            LiteDatabase db = null;

            var openError = Record.Exception(() => db = Open(data, log));
            if (openError != null)
            {
                AssertSafeDatabaseError(openError);
                data.ToArray().Should().Equal(scenario.OldData);
                return;
            }

            using (db)
            {
                var names = db.GetCollectionNames().OrderBy(x => x).ToArray();
                names.Should().Contain(name => name.StartsWith("late", StringComparison.Ordinal),
                    "the test must reach the stale-WAL DropCollection state reported in the issue");
                var failures = new List<Exception>();

                foreach (var name in names)
                {
                    try
                    {
                        db.DropCollection(name).Should().BeTrue(
                            "every name came from the same database's collection catalog");
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }

                foreach (var failure in failures)
                {
                    AssertSafeDatabaseError(failure);
                }

                var probeError = Record.Exception(() => db.GetCollection("transaction-probe").Insert(
                    new BsonDocument { ["_id"] = 1, ["value"] = "probe" }));
                if (probeError != null)
                {
                    AssertSafeDatabaseError(probeError);
                }
            }
        }

        private static Scenario CreateScenario()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] oldData;
            byte[] currentData;
            byte[] lateLog;

            using (var db = Open(data, log))
            {
                Fill(db, "base", 500);
                db.Checkpoint();
                oldData = data.ToArray();

                Fill(db, "mid", 300);
                db.GetCollection("base1").DeleteMany("$.n < 250");
                db.Checkpoint();

                db.CheckpointSize = 0;
                Fill(db, "late", 400);
                currentData = data.ToArray();
                lateLog = log.ToArray();
            }

            lateLog.Should().NotBeEmpty("the control must exercise WAL recovery rather than only the data file");
            currentData.Should().NotEqual(oldData);
            return new Scenario(oldData, currentData, lateLog);
        }

        private static PersistedDatabase DropAndReplace(byte[] dataBytes, byte[] logBytes, string[] expectedNames)
        {
            using var data = CopyIntoExpandableStream(dataBytes);
            using var log = CopyIntoExpandableStream(logBytes);
            using (var db = Open(data, log))
            {
                var names = db.GetCollectionNames().OrderBy(x => x).ToArray();
                names.Should().Equal(expectedNames.OrderBy(x => x));

                foreach (var name in names)
                {
                    db.GetCollection(name).Count().Should().BeGreaterThan(0);
                    db.DropCollection(name).Should().BeTrue();
                }

                InsertReplacement(db);
            }

            return new PersistedDatabase(data.ToArray(), log.ToArray());
        }

        private static void Fill(LiteDatabase db, string prefix, int count)
        {
            for (var collectionNumber = 0; collectionNumber < 4; collectionNumber++)
            {
                var name = prefix + collectionNumber;
                var collection = db.GetCollection(name);
                collection.EnsureIndex("n");
                collection.InsertBulk(Enumerable.Range(1, count).Select(i => new BsonDocument
                {
                    ["_id"] = i,
                    ["n"] = i,
                    ["tag"] = name + ":" + i,
                    ["payload"] = new string((char)('a' + collectionNumber), 300)
                }));
            }
        }

        private static void InsertReplacement(LiteDatabase db)
        {
            db.GetCollection("replacement").InsertBulk(new[]
            {
                new BsonDocument { ["_id"] = 101, ["value"] = "first replacement" },
                new BsonDocument { ["_id"] = 202, ["value"] = "second replacement" }
            }).Should().Be(2);
        }

        private static void AssertOldBackup(byte[] oldData)
        {
            using var data = CopyIntoExpandableStream(oldData);
            using var log = new MemoryStream();
            using var db = Open(data, log);

            db.GetCollectionNames().OrderBy(x => x).Should().Equal("base0", "base1", "base2", "base3");
            foreach (var name in db.GetCollectionNames())
            {
                db.GetCollection(name).Count().Should().Be(500);
                db.GetCollection(name).FindById(500)["tag"].AsString.Should().Be(name + ":500");
            }
        }

        private static void AssertReplacement(byte[] dataBytes, byte[] logBytes)
        {
            using var data = CopyIntoExpandableStream(dataBytes);
            using var log = CopyIntoExpandableStream(logBytes);
            using var db = Open(data, log);

            db.GetCollectionNames().Should().Equal("replacement");
            db.GetCollection("replacement").FindAll()
                .OrderBy(x => x["_id"].AsInt32)
                .Select(x => x["value"].AsString)
                .Should().Equal("first replacement", "second replacement");
        }

        private static LiteDatabase Open(Stream data, Stream log)
        {
            return new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = data,
                LogStream = log
            }));
        }

        private static void AssertSafeDatabaseError(Exception error)
        {
            error.Should().BeOfType<LiteException>();
            error.Message.Should().NotContain("{0}", "public errors must contain the actual page value");
            error.Message.Should().NotContain("already contains an open transaction",
                "a failed automatic transaction must always release its thread registration");
        }

        private static MemoryStream CopyIntoExpandableStream(byte[] bytes)
        {
            var stream = new MemoryStream(bytes.Length + 8192);
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private sealed class Scenario
        {
            public Scenario(byte[] oldData, byte[] currentData, byte[] lateLog)
            {
                OldData = oldData;
                CurrentData = currentData;
                LateLog = lateLog;
            }

            public byte[] OldData { get; }
            public byte[] CurrentData { get; }
            public byte[] LateLog { get; }
        }

        private sealed class PersistedDatabase
        {
            public PersistedDatabase(byte[] data, byte[] log)
            {
                Data = data;
                Log = log;
            }

            public byte[] Data { get; }
            public byte[] Log { get; }
        }
    }
}
