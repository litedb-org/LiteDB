using System;
using System.Collections.Generic;
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
        [Fact]
        public void DropCollection()
        {
            using (var db = DatabaseFactory.Create())
            {
                db.GetCollectionNames().Should().NotContain("col");

                var col = db.GetCollection("col");

                col.Insert(new BsonDocument {["a"] = 1});

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

        private class VectorDocument
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
        }

        [Fact]
        public void DropCollection_WithVectorIndex_Regression()
        {
            using var file = new TempFile();

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("vectors");
                var options = new VectorIndexOptions(8, VectorDistanceMetric.Cosine);

                collection.Insert(new List<VectorDocument>
                {
                    new VectorDocument { Id = 1, Embedding = new[] { 1f, 0.5f, -0.25f, 0.75f, 1.5f, -0.5f, 0.25f, -1f } },
                    new VectorDocument { Id = 2, Embedding = new[] { -0.5f, 0.25f, 0.75f, -1.5f, 1f, 0.5f, -0.25f, 0.125f } },
                    new VectorDocument { Id = 3, Embedding = new[] { 0.5f, -0.75f, 1.25f, 0.875f, -0.375f, 0.625f, -1.125f, 0.25f } }
                });

                collection.EnsureIndex("embedding_idx", x => x.Embedding, options);

                db.Checkpoint();

                Action drop = () => db.DropCollection("vectors");

                drop.Should().NotThrow(
                    "dropping a collection with vector indexes should release vector index pages instead of treating them like skip-list indexes");

                db.Checkpoint();
            }

            using (var reopened = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                reopened.GetCollectionNames().Should().NotContain("vectors");
            }
        }

    }
}