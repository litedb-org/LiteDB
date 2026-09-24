using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactSchemaRecovery_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("password", false)]
        [InlineData(null, true)]
        [InlineData("password", true)]
        public void Every_schema_rollover_or_drop_reuse_wal_boundary_recovers_complete_state(string password, bool drop)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var images = new List<(byte[] Data, byte[] Log, BsonDocument[] Expected)>();
            var original = Documents(0);
            var added = Documents(3);
            var expected = original;
            var initialSchemas = new HashSet<uint>();
            var writtenSchemas = new HashSet<uint>();
            var writtenPages = new HashSet<uint>();
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password,
                TransactionPageLimit = 2, DurableCommits = true
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                engine.SimulateDiskWriteFail = page =>
                {
                    if (page[BasePage.P_PAGE_TYPE] == (byte)PageType.Schema)
                        initialSchemas.Add(page.ReadUInt32(BasePage.P_PAGE_ID));
                };
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker");
                docs.Insert(original);
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["Payload"] = "keep" });
                db.Checkpoint();
                initialSchemas.Should().HaveCount(1, "the fixture fills one schema page before the transition");
                Verify(db, original); // Populate the read catalog before changing its page ownership.

                engine.SimulateDiskWriteFail = page =>
                {
                    images.Add((data.ToArray(), log.ToArray(), expected));
                    var id = page.ReadUInt32(BasePage.P_PAGE_ID);
                    writtenPages.Add(id);
                    if (page[BasePage.P_PAGE_TYPE] == (byte)PageType.Schema) writtenSchemas.Add(id);
                };
                if (drop)
                {
                    db.DropCollection("docs").Should().BeTrue();
                    expected = new BsonDocument[0];
                    images.Add((data.ToArray(), log.ToArray(), expected));
                    writtenPages.Clear();
                    writtenSchemas.Clear();
                    // Reclaim committed deleted pages in the next transaction.
                    db.BeginTrans();
                    docs.EnsureIndex("Marker");
                    docs.Insert(added);
                    db.Commit();
                    expected = added;
                    writtenPages.Intersect(initialSchemas).Should().NotBeEmpty("a reclaimed schema page must actually be reused");
                }
                else
                {
                    docs.Insert(added);
                    expected = original.Concat(added).ToArray();
                    writtenSchemas.Except(initialSchemas).Should().NotBeEmpty("the schema chain must actually roll over to another page");
                }
                images.Add((data.ToArray(), log.ToArray(), expected));
                engine.SimulateDiskWriteFail = null;
                Verify(db, expected);
            }
            images.Count.Should().BeGreaterThan(3);

            // Model process death before each WAL page write: install those exact
            // data/WAL bytes without disposing/checkpointing the captured engine.
            // Torn writes and lost flushes are covered by the promotion fault models.
            foreach (var image in images)
            {
                using var file = new TempFile();
                var logName = FileHelper.GetLogFile(file.Filename);
                try
                {
                    File.WriteAllBytes(file.Filename, image.Data);
                    File.WriteAllBytes(logName, image.Log);
                    foreach (var readOnly in new[] { true, false, false })
                    {
                        using var db = new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        });
                        Verify(db, image.Expected);
                        if (!readOnly) db.Checkpoint();
                    }
                }
                finally { File.Delete(logName); }
            }
        }

        private static void Verify(LiteDatabase db, BsonDocument[] expected)
        {
            var docs = db.GetCollection("docs");
            var actual = docs.FindAll().OrderBy(doc => doc["_id"].AsInt32).ToArray();
            actual.Length.Should().Be(expected.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                BsonSerializer.Serialize(actual[i]).Should().Equal(BsonSerializer.Serialize(expected[i]));
                var indexed = docs.Find(Query.EQ("Marker", expected[i]["Marker"])).ToArray();
                indexed.Should().HaveCount(1);
                BsonSerializer.Serialize(indexed[0]).Should().Equal(BsonSerializer.Serialize(expected[i]));
            }
            db.GetCollection("unrelated").FindById(1)["Payload"].AsString.Should().Be("keep");
        }

        private static BsonDocument[] Documents(int firstShape) => Enumerable.Range(firstShape * 2 + 1, 6).Select(id =>
        {
            var document = new BsonDocument { ["_id"] = id, ["Marker"] = id };
            for (var field = 0; field < 30; field++)
                document[$"Shape{(id - 1) / 2}Field{field}" + new string('x', 60)] = id * 100 + field;
            return document;
        }).ToArray();
    }
}
