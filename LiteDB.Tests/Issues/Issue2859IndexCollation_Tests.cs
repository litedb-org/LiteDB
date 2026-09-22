using System;
using System.Linq;
using System.IO;
using System.Globalization;
using LiteDB.Engine;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2859IndexCollation_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Legacy_binary_nested_indexes_are_rejected_when_incompatible(bool array, bool duplicate)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                var rows = db.GetCollection("rows");
                foreach (var value in duplicate ? new[] { "a", "A" } : new[] { "Zebra", "apple" })
                {
                    BsonValue key = array ? (BsonValue)new BsonArray { 1, value } : new BsonDocument { ["guard"] = 1, ["leaf"] = value };
                    rows.Insert(new BsonDocument { ["value"] = key });
                }
                rows.EnsureIndex("value", unique: true);
            }
            // Pre-fix nested keys always used binary order even in IgnoreCase files.
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            Array.Copy(BitConverter.GetBytes((int)CompareOptions.IgnoreCase), 0, bytes, EnginePragmas.P_COLLATION_SORT, 4);
            for (var page = 0; page < bytes.Length; page += Constants.PAGE_SIZE)
                PageChecksum.Write(new BufferSlice(bytes, page, Constants.PAGE_SIZE));
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }); };
            open.Should().Throw<LiteException>().WithMessage("*collation*Rebuild*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nested_index_keys_use_configured_collation_after_reopening(bool array)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Collation = new Collation("en-US/IgnoreCase") };
            BsonValue Key(string value) => array ? (BsonValue)new BsonArray { 1, value } : new BsonDocument { ["guard"] = 1, ["leaf"] = value };
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = Key("Zebra") });
                rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = Key("apple") });
                rows.EnsureIndex("value", unique: true).Should().BeTrue();
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.Find(Query.EQ("value", Key("APPLE"))).Select(row => row["_id"].AsInt32).Should().Equal(2);
                Action duplicate = () => rows.Insert(new BsonDocument { ["_id"] = 3, ["value"] = Key("APPLE") });
                duplicate.Should().Throw<LiteException>().WithMessage("*duplicate key*");
                rows.Count().Should().Be(2);
            }
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Indexed_nested_not_equal_matches_a_collection_scan(bool array)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            BsonValue Key(string value) => array ? (BsonValue)new BsonArray { 1, value } : new BsonDocument { ["leaf"] = value };
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = Key("APPLE") });
            rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = Key("pear") });
            var predicate = Query.Not("value", Key("apple"));
            rows.Find(predicate).Select(row => row["_id"].AsInt32).Should().Equal(2);
            rows.EnsureIndex("value");
            rows.Find(predicate).Select(row => row["_id"].AsInt32).Should().Equal(2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Inclusive_nested_ranges_keep_every_equivalent_key(bool array)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            BsonValue Key(string value) => array ? (BsonValue)new BsonArray { 1, value } : new BsonDocument { ["leaf"] = value };
            for (var id = 1; id <= 128; id++)
                rows.Insert(new BsonDocument { ["_id"] = id, ["value"] = Key(id % 2 == 0 ? "a" : "A") });
            rows.EnsureIndex("value");
            foreach (var key in new[] { Key("a"), Key("A") })
            foreach (var predicate in new[] { Query.Between("value", key, key), Query.GTE("value", key), Query.LTE("value", key) })
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                rows.Query().Where(predicate).OrderBy("value", order).ToArray()
                    .Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(Enumerable.Range(1, 128));
            }
        }
    }
}
