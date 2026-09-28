using System;
using System.Globalization;
using System.Linq;
using FluentAssertions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// An independent model of committed rows: a checkpointed batch plus inserts,
    /// updates and deletes that are committed only in the WAL, behind a unique index
    /// whose order differs from primary-key order.
    /// </summary>
    internal static class CommittedRows
    {
        internal const string Collection = "rows";
        private const int Checkpointed = 24;
        private const int Total = 40;
        private const int Updated = 8;
        private static readonly int[] Deleted = { 3, 30 };

        /// <summary>The row a test adds after recovery.</summary>
        internal static BsonDocument Extra => Document(Total + 1, 1);

        /// <summary>
        /// Checkpoints the first batch, then commits the rest to the WAL only. The
        /// checkpoint pragma stays 0, so closing does not backfill the WAL either.
        /// </summary>
        internal static void Write(LiteDatabase db)
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection(Collection);
            rows.EnsureIndex("key", "$.key", true);
            rows.InsertBulk(Enumerable.Range(1, Checkpointed).Select(id => Document(id, 1)));
            db.Checkpoint();
            rows.InsertBulk(Enumerable.Range(Checkpointed + 1, Total - Checkpointed).Select(id => Document(id, 1)));
            rows.Update(Enumerable.Range(1, Updated).Select(id => Document(id, 2))).Should().Be(Updated);
            foreach (var id in Deleted) rows.Delete(id).Should().BeTrue();
        }

        /// <summary>Every committed row with its complete payload, and the unique index results.</summary>
        internal static void Verify(LiteDatabase db, bool withExtra)
        {
            var expected = Enumerable.Range(1, Total).Where(id => !Deleted.Contains(id))
                .Select(id => Document(id, id <= Updated ? 2 : 1))
                .Concat(withExtra ? new[] { Extra } : new BsonDocument[0]).ToArray();
            var rows = db.GetCollection(Collection);
            rows.Query().OrderBy("_id").ToArray().Select(x => JsonSerializer.Serialize(x))
                .Should().Equal(expected.Select(x => JsonSerializer.Serialize(x)), "every committed row keeps its complete payload");

            var byKey = rows.Query().Where("key >= 'k'").OrderBy("key");
            byKey.GetPlan()["index"]["name"].AsString.Should().Be("key", "the unique index itself must be read");
            byKey.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(
                expected.OrderBy(x => x["key"].AsString, StringComparer.Ordinal).Select(x => x["_id"].AsInt32));
            foreach (var id in Deleted)
                (rows.FindOne(Query.EQ("key", Key(id))) is null).Should().BeTrue("deleted row {0} must stay deleted", id);
            rows.FindOne(Query.EQ("key", Key(Updated)))["version"].AsInt32.Should().Be(2);
            rows.FindOne(Query.EQ("key", Key(Total)))["payload"].AsString.Should().Be(Document(Total, 1)["payload"].AsString);
        }

        private static BsonDocument Document(int id, int version) => new BsonDocument
        {
            ["_id"] = id,
            ["key"] = Key(id),
            ["version"] = version,
            ["payload"] = new string((char)('a' + (id * 7 + version) % 26), 1800) + ":" + id + "/" + version
        };

        // A permutation of the ids: index order differs from primary-key order.
        private static string Key(int id) => "k" + (id * 17 % 101).ToString("D3", CultureInfo.InvariantCulture);
    }
}
