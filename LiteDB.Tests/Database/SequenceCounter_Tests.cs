using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class SequenceCounter_Tests
    {
        [Fact]
        public void AtomicCounter_ConcurrentMaxAndIncrement_PreserveHighWaterMark()
        {
            var counter = new SequenceCounter(0);
            var ids = new ConcurrentBag<long>();
            Parallel.For(0, 8, worker =>
            {
                counter.SetMax(worker * 10000);
                for (var i = 0; i < 1000; i++) ids.Add(counter.Next());
            });
            ids.Distinct().Should().HaveCount(8000);
            counter.Value.Should().BeGreaterOrEqualTo(71000);
            counter.SetMax(-1);
            counter.Next().Should().Be(counter.Value);
            new SequenceCounter(long.MaxValue).Next().Should().Be(long.MinValue);
        }

        [Fact]
        public void AtomicCounter_MixedNumericHistory_MatchesOriginalAlgorithmBytes()
        {
            var original = new ConcurrentDictionary<string, long>();
            original["docs"] = 0;
            var counter = new SequenceCounter(0);
            var random = new Random(5021);
            for (var i = 0; i < 4000; i++)
            {
                long expected;
                long actual;
                if (i % 3 == 0)
                {
                    var value = i % 11 == 0 ? long.MaxValue : random.Next(-1000, 1000);
                    expected = original.AddOrUpdate("docs", value, (_, old) => Math.Max(old, value));
                    counter.SetMax(value);
                    actual = counter.Value;
                }
                else
                {
                    expected = original.AddOrUpdate("docs", 1, (_, old) => unchecked(old + 1));
                    actual = counter.Next();
                }
                BsonSerializer.Serialize(new BsonDocument { ["_id"] = actual }).Should()
                    .Equal(BsonSerializer.Serialize(new BsonDocument { ["_id"] = expected }));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void NumericIds_RollbackFailureDropAndReopen_PreservePayloadsAndIndexes(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            using (var db = new LiteDatabase(connection))
            {
                var docs = db.GetCollection("docs", BsonAutoId.Int64);
                docs.EnsureIndex("token", unique: true);
                docs.Insert(new BsonDocument { ["_id"] = 100L, ["token"] = "ack" });
                db.BeginTrans().Should().BeTrue();
                docs.Insert(new BsonDocument { ["_id"] = 1000L, ["token"] = "abort" });
                db.Rollback().Should().BeTrue();
                // The old sequence cache deliberately keeps gaps after rollback.
                docs.Insert(new BsonDocument { ["token"] = "next" }).AsInt64.Should().Be(1001);
                Action duplicate = () => docs.Insert(new BsonDocument { ["_id"] = 2000L, ["token"] = "ack" });
                duplicate.Should().Throw<LiteException>();
                docs.Insert(new BsonDocument { ["token"] = "after-failure" }).AsInt64.Should().Be(2001);
                db.Execute("SELECT $ FROM $sequences").ToArray().Single(x => x["collection"] == "docs")["value"].AsInt64.Should().Be(2001);
                db.GetCollection("other", BsonAutoId.Int64).Insert(new BsonDocument { ["_id"] = 9000L });
                db.DropCollection("other").Should().BeTrue();
                db.GetCollection("OTHER", BsonAutoId.Int64).Insert(new BsonDocument()).AsInt64.Should().Be(1);
            }
            using var reopened = new LiteDatabase(connection);
            var rows = reopened.GetCollection("docs", BsonAutoId.Int64);
            rows.Insert(new BsonDocument { ["token"] = "reopened" }).AsInt64.Should().Be(2002);
            rows.FindAll().Select(d => d["_id"].AsInt64).OrderBy(x => x).Should().Equal(100, 1001, 2001, 2002);
            rows.Find(Query.EQ("token", "ack")).Single()["_id"].AsInt64.Should().Be(100);
            rows.Find(Query.EQ("token", "abort")).Should().BeEmpty();
        }

        [Fact]
        public void SharedDirectFacades_ConcurrentAutoIds_AreUniqueAndSurviveReopen()
        {
            using var file = new TempFile();
            using (var first = new LiteDatabase(file.Filename))
            {
                first.GetCollection("docs", BsonAutoId.Int64).Insert(new BsonDocument { ["_id"] = 500L });
                Parallel.For(0, 4, worker =>
                {
                    using var db = new LiteDatabase(file.Filename);
                    var docs = db.GetCollection("docs", BsonAutoId.Int64);
                    for (var i = 0; i < 50; i++) docs.Insert(new BsonDocument { ["worker"] = worker });
                });
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("docs").FindAll().Select(d => d["_id"].AsInt64).OrderBy(x => x)
                .Should().Equal(Enumerable.Range(500, 201).Select(x => (long)x));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void LostWalSuffix_RepeatedRecovery_RebuildsSequenceFromCommittedIndex(string password)
        {
            using var source = new LiteDB.Internals.WalTestDatabase(password);
            var docs = source.Database.GetCollection("docs", BsonAutoId.Int64);
            source.Database.CheckpointSize = 0;
            docs.EnsureIndex("token", unique: true);
            docs.Insert(new BsonDocument { ["_id"] = 100L, ["token"] = "ack" });
            var data = source.Data.ToArray();
            var acknowledgedLog = source.Log.ToArray();
            docs.Insert(new BsonDocument { ["_id"] = 9000L, ["token"] = "lost" });
            // Model loss of every byte of the later WAL suffix, retaining the
            // complete earlier acknowledged WAL. No graceful source disposal.
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, data);
            var logName = Path.Combine(Path.GetDirectoryName(file.Filename),
                Path.GetFileNameWithoutExtension(file.Filename) + "-log" + Path.GetExtension(file.Filename));
            File.WriteAllBytes(logName, acknowledgedLog);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var recovered = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                var rows = recovered.GetCollection("docs", BsonAutoId.Int64);
                rows.FindById(100L)["token"].AsString.Should().Be("ack");
                rows.Find(Query.EQ("token", "lost")).Should().BeEmpty();
                rows.Insert(new BsonDocument { ["token"] = "recovery" + reopen }).AsInt64.Should().Be(101 + reopen);
                rows.Count().Should().Be(2 + reopen);
            }
        }
    }
}
