using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2525_Tests
    {
        private static string NewTempDbPath()
            => Path.Combine(Path.GetTempPath(), $"LiteDB_FindAllLoop_{Guid.NewGuid():N}.db");

        private static LiteEngine NewEngine(string path)
            => new LiteEngine(new EngineSettings { Filename = path });

        private static void InsertPeopleWithEngine(LiteEngine engine, string collection, IEnumerable<(int id, string name)> rows)
        {
            var docs = rows.Select(r => new BsonDocument { ["_id"] = r.id, ["name"] = r.name });
            engine.Insert(collection, docs, BsonAutoId.Int32);
        }

        private static void CreateIndexSelfLoop(LiteEngine engine, string collection, string indexName)
        {
            engine.BeginTrans();
            var monitorField = typeof(LiteEngine).GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var monitor = (TransactionMonitor)monitorField.GetValue(engine);

            var tx = monitor.GetTransaction(create: false, queryOnly: false, out _);
            Assert.NotNull(tx);

            var snapshot = tx.CreateSnapshot(LockMode.Write, collection, addIfNotExists: false);
            Assert.NotNull(snapshot);
            Assert.NotNull(snapshot.CollectionPage);

            CollectionIndex ci = indexName == "_id"
                ? snapshot.CollectionPage.PK
                : snapshot.CollectionPage.GetCollectionIndex(indexName);

            Assert.NotNull(ci);

            var headPage = snapshot.GetPage<IndexPage>(ci.Head.PageID);
            var headNode = headPage.GetIndexNode(ci.Head.Index);
            var firstAddr = headNode.Next[0];
            Assert.False(firstAddr.IsEmpty);
            var firstPage = snapshot.GetPage<IndexPage>(firstAddr.PageID);
            var firstNode = firstPage.GetIndexNode(firstAddr.Index);
            firstNode.SetNext(0, firstNode.Position);
            tx.Commit();
        }

        [Fact]
        public void PK_Loop_Should_Throw_On_EnsureIndex()
        {
            var path = NewTempDbPath();
            try
            {
                using (var engine = NewEngine(path))
                {
                    InsertPeopleWithEngine(engine, "col", new[]
                    {
                        (1, "a"),
                        (2, "b"),
                        (3, "c")
                    });
                }

                using (var engine = NewEngine(path))
                {
                    CreateIndexSelfLoop(engine, "col", "_id");
                }

                using (var db = new LiteDatabase(path))
                {
                    var col = db.GetCollection("col");
                    var ex = Record.Exception(() =>
                    {
                        col.EnsureIndex("name"); // albo Find(Query.All("name")), zależnie od testu
                    });

                    Assert.NotNull(ex);
                    Assert.Contains("Detected loop in FindAll", ex.Message, StringComparison.OrdinalIgnoreCase);

                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Secondary_Index_Loop_Should_Throw_On_Query_Then_Rebuild_Fixes()
        {
            var path = NewTempDbPath();
            var backup = path + "-backup";
            try
            {
                using (var engine = NewEngine(path))
                {
                    InsertPeopleWithEngine(engine, "col", new[]
                    {
                        (1, "a"),
                        (2, "b"),
                        (3, "c")
                    });
                }

                using (var db = new LiteDatabase(path))
                {
                    var col = db.GetCollection("col");
                    col.EnsureIndex("name");
                }

                using (var engine = NewEngine(path))
                {
                    CreateIndexSelfLoop(engine, "col", "name");
                }

                using (var db = new LiteDatabase(path))
                {
                    var ex = Record.Exception(() =>
                    {
                        var _ = db.GetCollection("col").Query().OrderBy("name").ToList();
                    });

                    Assert.NotNull(ex);
                    Assert.Contains("Detected loop in FindAll", ex.Message, StringComparison.OrdinalIgnoreCase);

                }

                using (var db = new LiteDatabase(path))
                {
                    db.Rebuild();
                }

                using (var db = new LiteDatabase(path))
                {
                    var col = db.GetCollection("col");
                    var list = col.Find(Query.All("name")).ToList();

                    Assert.Equal(3, list.Count);
                    Assert.Equal(new[] { "a", "b", "c" }, list.Select(x => x["name"].AsString).OrderBy(x => x).ToArray());
                }

                if (File.Exists(backup)) File.Delete(backup);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(backup)) File.Delete(backup);
            }
        }
    }
}
