using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class ChecksumStorageModel_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RepeatedGenerations_WithPageReuseAndSectorTears_PreserveCommittedModel(string password)
        {
            var random = new Random(2935);
            var expected = new Dictionary<int, BsonDocument>();
            using var data = new CapturedData();
            using var log = new MemoryStream();
            data.Log = log;
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 1
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            docs.EnsureIndex("score");
            docs.EnsureIndex("token", unique: true);
            var revision = 0;
            for (var generation = 0; generation < 6; generation++)
            {
                for (var transaction = 0; transaction < 4; transaction++)
                {
                    var next = new Dictionary<int, BsonDocument>(expected);
                    db.BeginTrans().Should().BeTrue();
                    for (var operation = 0; operation < 12; operation++)
                    {
                        var id = random.Next(32);
                        if (random.Next(3) == 0)
                        {
                            docs.Delete(id).Should().Be(next.Remove(id));
                        }
                        else
                        {
                            revision++;
                            var doc = Document(id, revision, random.Next(-2, 3), random.Next(3));
                            docs.Upsert(doc);
                            next[id] = doc;
                        }
                    }
                    if (transaction == 3) db.Rollback().Should().BeTrue();
                    else
                    {
                        db.Commit().Should().BeTrue();
                        expected = next;
                    }
                }
                AssertModel(db, expected);
                VerifyRecovery(data.ToArray(), log.ToArray(), password, expected);
                data.Writes.Clear();
                data.Capture = true;
                db.Checkpoint();
                data.Capture = false;
                data.Writes.Should().NotBeEmpty();
                var headerPosition = password == null ? 0 : PAGE_SIZE;
                // Cover every header write and regularly spaced non-header writes
                // across reused index/data/overflow pages, keeping CI bounded.
                foreach (var write in data.Writes.Where((x, i) => x.Position == headerPosition || i % 7 == 0))
                {
                    foreach (var sector in new[] { 16, 512 })
                    {
                        var torn = new byte[Math.Max(write.Before.Length, write.Position + write.Bytes.Length)];
                        Buffer.BlockCopy(write.Before, 0, torn, 0, write.Before.Length);
                        // Preserve alternating old/new sectors, including the end
                        // of the page: this is not a prefix-only torn-write model.
                        for (var offset = 0; offset < write.Bytes.Length; offset += sector * 2)
                            Buffer.BlockCopy(write.Bytes, offset, torn, write.Position + offset, sector);
                        VerifyRecovery(torn, write.Wal, password, expected);
                    }
                }
                // Before the data sync, storage can persist any subset of the
                // batch, including a complete header ahead of its data pages.
                // Exclude the final salt write: it follows a successful sync.
                data.Writes.Last().Position.Should().Be(headerPosition);
                var reordered = new byte[data.Writes.Last().Before.Length];
                Buffer.BlockCopy(data.Writes[0].Before, 0, reordered, 0, data.Writes[0].Before.Length);
                for (var i = 0; i < data.Writes.Count - 1; i++)
                {
                    var write = data.Writes[i];
                    if (write.Position == headerPosition || i % 2 == 0)
                        Buffer.BlockCopy(write.Bytes, 0, reordered, write.Position, write.Bytes.Length);
                }
                VerifyRecovery(reordered, data.Writes.Last().Wal, password, expected);
                // A completed checkpoint must reject a WAL from its old salt.
                VerifyRecovery(data.ToArray(), data.Writes.Last().Wal, password, expected);
            }
        }

        private static BsonDocument Document(int id, int revision, int score, int size)
        {
            return new BsonDocument
            {
                ["_id"] = id, ["revision"] = revision, ["score"] = score,
                ["token"] = id + ":" + revision,
                ["payload"] = new string((char)('a' + revision % 26), new[] { 100, 2000, 9000 }[size])
            };
        }

        private static void VerifyRecovery(byte[] image, byte[] wal, string password, Dictionary<int, BsonDocument> expected)
        {
            using var data = ChecksumTestFiles.Copy(image);
            using var log = ChecksumTestFiles.Copy(wal);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = true };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false)) AssertModel(db, expected);
            data.ToArray().Should().Equal(image);
            log.ToArray().Should().Equal(wal);
            settings.ReadOnly = false;
            var continued = new Dictionary<int, BsonDocument>(expected) { [1000] = Document(1000, 1, 0, 2) };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AssertModel(db, expected);
                db.GetCollection("docs").Insert(continued[1000]);
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            AssertModel(reopened, continued);
        }

        private static void AssertModel(LiteDatabase db, Dictionary<int, BsonDocument> expected)
        {
            var docs = db.GetCollection("docs");
            var actual = docs.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            actual.Should().Equal(expected.OrderBy(x => x.Key).Select(x => x.Value));
            for (var score = -2; score <= 2; score++)
            {
                var ids = expected.Where(x => x.Value["score"].AsInt32 == score).Select(x => x.Key).OrderBy(x => x);
                docs.Find(Query.EQ("score", score)).Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(ids);
            }
            foreach (var document in expected.Values)
                Assert.Equal(document, docs.FindOne(Query.EQ("token", document["token"])));
        }

        private sealed class CapturedData : MemoryStream
        {
            internal bool Capture;
            internal MemoryStream Log;
            internal readonly List<Image> Writes = new List<Image>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Capture && count == PAGE_SIZE)
                {
                    var bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                    Writes.Add(new Image { Position = checked((int)Position), Before = ToArray(), Bytes = bytes, Wal = Log.ToArray() });
                }
                base.Write(buffer, offset, count);
            }
        }

        private sealed class Image
        {
            internal int Position;
            internal byte[] Before;
            internal byte[] Bytes;
            internal byte[] Wal;
        }
    }
}
