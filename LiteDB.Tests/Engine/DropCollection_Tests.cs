using System;
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
        private class VectorDocument
        {
            public int Id { get; set; }

            public float[] Embedding { get; set; }
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
            using var tempFile = new TempFile();

            using (var db = new LiteDatabase(tempFile.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("vectors");
                var dimensions = 192;
                var options = new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine);

                var documents = new List<VectorDocument>();

                for (var i = 0; i < 64; i++)
                {
                    var embedding = new float[dimensions];

                    for (var j = 0; j < dimensions; j++)
                    {
                        embedding[j] = (float)Math.Cos((i + 1) * (j + 1) * 0.03125d);
                    }

                    documents.Add(new VectorDocument
                    {
                        Id = i + 1,
                        Embedding = embedding
                    });
                }

                collection.Insert(documents);
                collection.EnsureIndex("embedding_idx", x => x.Embedding, options);

                db.Checkpoint();

                var fileInfo = new FileInfo(tempFile.Filename);
                var sizeAfterInsert = fileInfo.Length;
                sizeAfterInsert.Should().BeGreaterThan(0);

                var drop = () => db.DropCollection("vectors");

                drop.Should().NotThrow("dropping a collection that owns vector indexes should release all associated pages");

                db.Checkpoint();

                fileInfo.Refresh();
                var sizeAfterDrop = fileInfo.Length;

                sizeAfterDrop.Should().BeLessThan(sizeAfterInsert, "vector index pages must be reclaimed when the collection is dropped");
            }
        }
    }
}
