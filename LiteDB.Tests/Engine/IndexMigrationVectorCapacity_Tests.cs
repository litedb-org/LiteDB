using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationVectorCapacity_Tests
    {
        [Theory]
        [InlineData(null, 2)]
        [InlineData("capacity-password", 2)]
        [InlineData(null, 4096)]
        [InlineData("capacity-password", 4096)]
        public void Computed_vector_budget_is_checked_before_promotion(string password, int dimensions)
        {
            using var file = new TempFile();
            var vector = Enumerable.Repeat(1f, dimensions).ToArray();
            const string expression = "COALESCE($.v, $.fallback)";
            using (var db = IndexMigration_Tests.Open(file.Filename, password))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 8).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["v"] = new BsonVector(vector)
                }));
                rows.EnsureIndex("computedVector", expression, new VectorIndexOptions((ushort)dimensions));
                db.Checkpoint();
                db.LimitSize = new FileInfo(file.Filename).Length;
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 9;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            var before = File.ReadAllBytes(file.Filename);
            Action rejected = () => { using var db = IndexMigration_Tests.Open(file.Filename, password); };
            rejected.Should().Throw<LiteException>().WithMessage("*capacity*IndexMigrationLimitSize*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = password, IndexMigrationLimitSize = 8 * 1024 * 1024
            }))
            {
                var rows = db.GetCollection("rows");
                rows.Count().Should().Be(8);
                rows.Query().TopKNear(BsonExpression.Create(expression), vector, 8).ToArray().Should().HaveCount(8);
                db.Checkpoint();
            }
            using var reopened = IndexMigration_Tests.Open(file.Filename, password, true);
            reopened.GetCollection("rows").Query().TopKNear(BsonExpression.Create(expression), vector, 8)
                .ToArray().Should().HaveCount(8);
        }
    }
}
