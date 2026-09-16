using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344StorageCompatibility_Tests
    {
        [Fact]
        public void Metadata_update_preserves_an_active_chunk_cursor()
        {
            using var db = new LiteDatabase(":memory:");
            using var source = new MemoryStream(new byte[LiteFileStream<string>.MAX_CHUNK_SIZE + 1]);
            db.FileStorage.Upload("file", "file", source);
            Assert.True(db.BeginTrans());
            using (var cursor = db.GetCollection("_chunks").FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.True(db.FileStorage.SetMetadata("file", new BsonDocument { ["changed"] = true }));
                Assert.True(cursor.MoveNext());
                Assert.False(cursor.MoveNext());
            }
            Assert.True(db.Commit());
            Assert.True(db.FileStorage.FindById("file").Metadata["changed"].AsBoolean);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_file_operations_do_not_create_a_chunk_collection(bool delete)
        {
            using var db = new LiteDatabase(":memory:");
            using (var source = new MemoryStream()) db.FileStorage.Upload("empty", "file", source);
            Assert.DoesNotContain("_chunks", db.GetCollectionNames());
            if (delete) Assert.True(db.FileStorage.Delete("empty"));
            else Assert.True(db.FileStorage.SetMetadata("empty", new BsonDocument { ["changed"] = true }));
            Assert.DoesNotContain("_chunks", db.GetCollectionNames());
        }

        [Fact]
        public void Invalid_open_write_does_not_create_collections()
        {
            using var db = new LiteDatabase(":memory:");
            Assert.ThrowsAny<System.Exception>(() => db.FileStorage.OpenWrite("file", null));
            Assert.Empty(db.GetCollectionNames());
        }
    }
}
