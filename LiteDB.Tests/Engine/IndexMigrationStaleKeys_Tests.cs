using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Released Update diffs old and new index keys with <c>==</c>, i.e. the old
    /// decimal-rounded numeric and null-as-missing document equality. When a value
    /// changed between keys that were "equal" there (19.99 double -> 19.99m decimal,
    /// {a:null} -> {b:null}, 7 -> 7.0000000000000009), the node and its old key were kept
    /// while the document changed, including the primary key. Migration reorders
    /// member-path indexes by their stored keys, so it must first regenerate such keys
    /// from the documents or those documents become unreachable by seek.
    /// </summary>
    public class IndexMigrationStaleKeys_Tests
    {
        private static readonly MethodInfo AutoTransactionMethod =
            typeof(LiteEngine).GetMethod("AutoTransaction", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly string[] RowIndexes = { "price", "meta", "tags", "code", "n" };

        [Theory]
        [InlineData(null)]
        [InlineData(LegacyIndexFixtures.Password)]
        public void Keys_left_behind_by_released_updates_are_regenerated_during_migration(string password)
        {
            using var file = LegacyIndexFixtures.Extract("stale", password);
            for (var open = 0; open < 2; open++)
            {
                // The first open migrates; the second proves the repaired keys persisted.
                using var db = IndexMigration_Tests.Open(file.Filename, password);
                var rows = db.GetCollection("rows");
                rows.Count(Query.EQ("price", 19.99m)).Should().Be(1, "the document holds decimal 19.99");
                rows.Count(Query.EQ("price", 19.99)).Should().Be(0, "binary64 19.99 differs from decimal 19.99 in v11");
                rows.Count(Query.EQ("meta", new BsonDocument { ["b"] = BsonValue.Null })).Should().Be(1);
                rows.Count(Query.EQ("meta", new BsonDocument { ["a"] = BsonValue.Null })).Should().Be(0);
                rows.Count(Query.EQ("tags", new BsonArray { 1.0000000000000002 })).Should().Be(1);
                rows.Count(Query.EQ("code", 0.1m)).Should().Be(1, "the unique index holds the document's decimal");
                rows.Count(Query.EQ("code", 0.1)).Should().Be(0);
                rows.Count(Query.EQ("n", 1.0000000000000002)).Should().Be(1);
                rows.Count(Query.EQ("n", 1)).Should().Be(0);
                ShouldReachEveryDocumentThroughEveryIndex(rows);

                var ids = db.GetCollection("ids");
                ids.FindById(0.1m)["v"].AsInt32.Should().Be(2);
                (ids.FindById(0.1) == null).Should().BeTrue("binary64 0.1 is not the stored decimal _id");
                ids.FindById(7.0000000000000009)["v"].AsInt32.Should().Be(8);
                (ids.FindById(7) == null).Should().BeTrue("7 is not the stored _id");
                ids.FindAll().Select(x => x["_id"]).Should().OnlyContain(id => ids.FindById(id) != null);
                db.Checkpoint();
            }

            using (var db = IndexMigration_Tests.Open(file.Filename, password))
            {
                var ids = db.GetCollection("ids");
                ids.Delete(0.1m).Should().BeTrue();
                ids.Delete(7.0000000000000009).Should().BeTrue();
                ids.Count().Should().Be(0);
                db.GetCollection("rows").DeleteMany(Query.EQ("price", 19.99m)).Should().Be(1);
            }
        }

        [Fact]
        public void Migrated_member_path_indexes_agree_with_documents_changed_by_legacy_updates()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
            {
                engine.EnsureIndex("rows", "price", BsonExpression.Create("$.price"), false);
                engine.EnsureIndex("rows", "meta", BsonExpression.Create("$.meta"), false);
                engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1, ["price"] = 19.99, ["meta"] = new BsonDocument { ["a"] = BsonValue.Null } } }, BsonAutoId.Int32);
                engine.Insert("ids", new[] { new BsonDocument { ["_id"] = 0.1, ["v"] = 1 } }, BsonAutoId.Int32);

                // What the legacy Update left behind: new document bytes, old index keys.
                RewriteDocument(engine, "rows", 1, new BsonDocument { ["_id"] = 1, ["price"] = 19.99m, ["meta"] = new BsonDocument { ["b"] = BsonValue.Null } });
                RewriteDocument(engine, "ids", 0.1, new BsonDocument { ["_id"] = 0.1m, ["v"] = 2 });
                engine.Checkpoint();
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });

            using var db = IndexMigration_Tests.Open(file.Filename, null);
            var rows = db.GetCollection("rows");
            rows.FindAll().Single()["price"].IsDecimal.Should().BeTrue();
            rows.Count(Query.EQ("price", 19.99m)).Should().Be(1, "the only document holds decimal 19.99");
            rows.Count(Query.EQ("price", 19.99)).Should().Be(0, "binary64 19.99 differs from decimal 19.99 in v11");
            rows.Count(Query.EQ("meta", new BsonDocument { ["b"] = BsonValue.Null })).Should().Be(1);

            var ids = db.GetCollection("ids");
            var id = ids.FindAll().Single()["_id"];
            id.IsDecimal.Should().BeTrue();
            (ids.FindById(id) != null).Should().BeTrue("a document must be reachable by its own _id");
            ids.Delete(id).Should().BeTrue();
        }

        [Fact]
        public void Unique_keys_that_collide_only_after_regeneration_reject_migration_without_changes()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
            {
                engine.EnsureIndex("rows", "code", BsonExpression.Create("$.code"), true);
                engine.Insert("rows", new[]
                {
                    new BsonDocument { ["_id"] = 1, ["code"] = 0.1 },
                    new BsonDocument { ["_id"] = 2, ["code"] = 0.2 }
                }, BsonAutoId.Int32);
                // Both documents now hold the same value under stale, distinct keys.
                RewriteDocument(engine, "rows", 2, new BsonDocument { ["_id"] = 2, ["code"] = 0.1m });
                RewriteDocument(engine, "rows", 1, new BsonDocument { ["_id"] = 1, ["code"] = 0.1m });
                engine.Checkpoint();
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            var before = System.IO.File.ReadAllBytes(file.Filename);

            Action open = () => IndexMigration_Tests.Open(file.Filename, null).Dispose();
            open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INDEX_DUPLICATE_KEY);
            System.IO.File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        private static void ShouldReachEveryDocumentThroughEveryIndex(ILiteCollection<BsonDocument> rows)
        {
            foreach (var document in rows.FindAll().ToList())
                foreach (var field in RowIndexes)
                    rows.Find(Query.EQ(field, document[field])).Select(x => x["_id"])
                        .Should().Contain(document["_id"], "{0} must be reachable through index {1}", document["_id"], field);
        }

        private static void RewriteDocument(LiteEngine engine, string collection, BsonValue id, BsonDocument document)
        {
            Func<TransactionService, bool> rewrite = transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
                var node = indexer.Find(snapshot.CollectionPage.PK, id, false, Query.Ascending);
                new DataService(snapshot, uint.MaxValue).Update(snapshot.CollectionPage, node.DataBlock, document);
                return true;
            };
            AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(engine, new object[] { rewrite });
        }
    }
}
