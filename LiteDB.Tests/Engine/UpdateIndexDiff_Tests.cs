using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class UpdateIndexDiff_Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(80)]
        public void Multikey_updates_match_independent_model_and_reopen(int width)
        {
            using var file = new TempFile();
            var expected = new Dictionary<int, int[]>();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("Keys");
                rows.EnsureIndex("Other");
                for (var id = 1; id <= 24; id++)
                {
                    var keys = Enumerable.Range(id, width).ToArray();
                    expected[id] = keys;
                    rows.Insert(Row(id, keys));
                }
                for (var round = 0; round < 4; round++)
                {
                    foreach (var id in expected.Keys.ToArray())
                    {
                        // Unchanged, reordered/duplicated, partially overlapping, then empty.
                        var keys = round == 0 ? expected[id] : round == 1 ?
                            expected[id].Reverse().Concat(expected[id]).ToArray() : round == 2 ?
                            Enumerable.Range(id + 5, width).ToArray() : new int[0];
                        expected[id] = keys;
                        rows.Update(Row(id, keys)).Should().BeTrue();
                    }
                    Verify(rows, expected);
                }
                // Leave nonempty changed keys to exercise persistence, not just deletion.
                foreach (var id in expected.Keys.ToArray())
                {
                    expected[id] = Enumerable.Range(id + 3, width).ToArray();
                    rows.Update(Row(id, expected[id])).Should().BeTrue();
                }
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(file.Filename);
                Verify(db.GetCollection("rows"), expected);
                db.Checkpoint();
            }
        }

        [Fact]
        public void Collation_equivalent_and_changed_unique_keys_preserve_results()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Collation = new Collation("en-US/IgnoreCase")
            }))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("Key", true);
                rows.Insert(new BsonDocument { ["_id"] = 1, ["Key"] = "alpha" });
                foreach (var key in new[] { "alpha", "ALPHA", "beta" })
                {
                    rows.Update(new BsonDocument { ["_id"] = 1, ["Key"] = key }).Should().BeTrue();
                    rows.Find(Query.EQ("Key", key.ToUpperInvariant())).Select(x => x["_id"].AsInt32).Should().Equal(1);
                }
                rows.Find(Query.EQ("Key", "alpha")).Should().BeEmpty();
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Find(Query.EQ("Key", "BETA")).Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unique_failure_rolls_back_data_and_all_index_changes(bool explicitTransaction)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("Other", true);
                rows.EnsureIndex("Keys");
                rows.Insert(new[] { Row(1, new[] { 1, 2 }), Row(2, new[] { 3, 4 }) });
                if (explicitTransaction) db.BeginTrans();
                var first = Row(1, new[] { 8, 9 });
                first["Other"] = 10;
                var second = Row(2, new[] { 11, 12 });
                second["Other"] = 10;
                Action update = () => rows.Update(new[] { first, second });
                update.Should().Throw<LiteException>();
                if (explicitTransaction) db.Rollback();
                Verify(rows, new Dictionary<int, int[]> { [1] = new[] { 1, 2 }, [2] = new[] { 3, 4 } });
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(file.Filename);
                Verify(db.GetCollection("rows"), new Dictionary<int, int[]> { [1] = new[] { 1, 2 }, [2] = new[] { 3, 4 } });
            }
        }

        [Fact]
        public void Moving_index_keys_during_update_many_visits_each_document_once()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("Other", true);
                rows.Insert(Enumerable.Range(1, 200).Select(id => Row(id, new[] { id })));
                rows.UpdateMany("{ Other: Other + 1000 }", "Other > 0").Should().Be(200);
                rows.Find(Query.Between("Other", 1001, 1200)).Select(x => x["_id"].AsInt32)
                    .Should().BeEquivalentTo(Enumerable.Range(1, 200));
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Should().OnlyContain(x => x["Other"].AsInt32 == x["_id"].AsInt32 + 1000);
        }

        [Fact]
        public void Secondary_key_changes_coexist_with_vector_updates_and_reopen()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection<VectorRow>("vectors");
                rows.EnsureIndex(x => x.Key);
                rows.EnsureIndex("embedding", x => x.Embedding, new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
                rows.Insert(new[] { new VectorRow { Id = 1, Key = 1, Embedding = new[] { 1f, 0f } },
                    new VectorRow { Id = 2, Key = 2, Embedding = new[] { 0f, 1f } } });
                rows.Update(new VectorRow { Id = 1, Key = 3, Embedding = new[] { 0f, 2f } }).Should().BeTrue();
                rows.Find(x => x.Key == 1).Should().BeEmpty();
                rows.Find(x => x.Key == 3).Select(x => x.Id).Should().Equal(1);
                rows.Query().TopKNear(x => x.Embedding, new[] { 0f, 2f }, 1).ToArray().Select(x => x.Id).Should().Equal(1);
            }
            using var reopened = new LiteDatabase(file.Filename);
            var vectors = reopened.GetCollection<VectorRow>("vectors");
            vectors.Find(x => x.Key == 3).Select(x => x.Id).Should().Equal(1);
            vectors.Query().TopKNear(x => x.Embedding, new[] { 0f, 2f }, 1).ToArray().Select(x => x.Id).Should().Equal(1);
        }

        public class VectorRow
        {
            public int Id { get; set; }
            public int Key { get; set; }
            public float[] Embedding { get; set; }
        }

        private static BsonDocument Row(int id, int[] keys) => new BsonDocument
        {
            ["_id"] = id, ["Keys"] = new BsonArray(keys.Select(x => new BsonValue(x))), ["Other"] = id
        };

        private static void Verify(ILiteCollection<BsonDocument> rows, Dictionary<int, int[]> expected)
        {
            rows.Count().Should().Be(expected.Count);
            foreach (var item in expected)
            {
                rows.FindById(item.Key)["Keys"].AsArray.Select(x => x.AsInt32).Should().Equal(item.Value);
                rows.Find(Query.EQ("Other", item.Key)).Select(x => x["_id"].AsInt32).Should().Equal(item.Key);
            }
            for (var key = 0; key < 110; key++)
            {
                var probe = key;
                rows.Find(Query.EQ("Keys", probe)).Select(x => x["_id"].AsInt32)
                    .Should().BeEquivalentTo(expected.Where(x => x.Value.Contains(probe)).Select(x => x.Key));
            }
        }
    }
}
