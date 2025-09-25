using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DropCollection_Tests
    {
        private static Dictionary<PageType, int> CountPagesByType(string filename)
        {
            var counts = new Dictionary<PageType, int>();
            var buffer = new byte[Constants.PAGE_SIZE];

            using var stream = File.OpenRead(filename);

            while (stream.Read(buffer, 0, buffer.Length) == buffer.Length)
            {
                var pageType = (PageType)buffer[BasePage.P_PAGE_TYPE];
                counts.TryGetValue(pageType, out var current);
                counts[pageType] = current + 1;
            }

            return counts;
        }

        [Fact]
        public void DropCollection()
        {
            using (var db = DatabaseFactory.Create())
            {
                db.GetCollectionNames().Should().NotContain("col");

                var col = db.GetCollection("col");

                col.Insert(new BsonDocument { ["a"] = 1 });

                db.GetCollectionNames().Should().Contain("col");

                db.DropCollection("col");

                db.GetCollectionNames().Should().NotContain("col");
            }
        }

        [Fact]
        public void InsertDropCollection()
        {
            using (var file = new TempFile())
            {
                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                    db.DropCollection("test");
                    db.Rebuild();
                }

                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                }
            }
        }

        [Fact]
        public void DropCollection_WithVectorIndex_Regression()
        {
            using var file = new TempFile();

            const ushort dimensions = 6;

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection("docs");

                collection.EnsureIndex(
                    "embedding_idx",
                    BsonExpression.Create("$.embedding"),
                    new VectorIndexOptions(dimensions, VectorDistanceMetric.Cosine));

                for (var i = 0; i < 8; i++)
                {
                    var embedding = new BsonArray(Enumerable.Range(0, dimensions)
                        .Select(j => new BsonValue(i + (j * 0.1))));

                    collection.Insert(new BsonDocument
                    {
                        ["_id"] = i + 1,
                        ["embedding"] = embedding
                    });
                }

                db.Checkpoint();
            }

            var beforeCounts = CountPagesByType(file.Filename);
            beforeCounts.TryGetValue(PageType.VectorIndex, out var vectorPagesBefore);
            vectorPagesBefore.Should().BeGreaterThan(0, "creating a vector index should allocate vector pages");

            var drop = () =>
            {
                using var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename);
                db.DropCollection("docs");
                db.Checkpoint();
            };

            drop.Should().NotThrow();

            var afterCounts = CountPagesByType(file.Filename);
            afterCounts.TryGetValue(PageType.VectorIndex, out var vectorPagesAfter);
            vectorPagesAfter.Should().BeLessThan(vectorPagesBefore, "dropping the collection should reclaim vector pages");
        }
    }
}
