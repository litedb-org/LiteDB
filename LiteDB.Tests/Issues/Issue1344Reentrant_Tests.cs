using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Reentrant_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Storage_writes_join_an_auto_transaction_without_changing_its_owner(ConnectionType mode)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = mode }))
            {
                using (var source = new MemoryStream(new byte[] { 1 })) db.FileStorage.Upload("delete", "old", source);
                using (var source = new MemoryStream(new byte[] { 2 })) db.FileStorage.Upload("metadata", "kept", source);
                IEnumerable<BsonDocument> batch()
                {
                    Assert.False(db.FileStorage.Delete("missing"));
                    Assert.True(db.FileStorage.Delete("delete"));
                    Assert.True(db.FileStorage.SetMetadata("metadata", new BsonDocument { ["answer"] = 42 }));
                    yield return new BsonDocument { ["_id"] = 1 };
                }
                Assert.Equal(1, db.GetCollection("rows").Insert(batch()));
                Assert.Equal(42, db.FileStorage.FindById("metadata").Metadata["answer"].AsInt32);
                Assert.False(db.FileStorage.Exists("delete"));
            }
            using var reopened = new LiteDatabase(file.Filename);
            Assert.Equal(1, reopened.GetCollection("rows").Count());
            Assert.True(reopened.FileStorage.Exists("metadata"));
        }

        [Fact]
        public void OpenWrite_keeps_a_file_cursor_valid_until_the_writer_is_flushed()
        {
            using var db = new LiteDatabase(":memory:");
            for (var i = 0; i < 3; i++)
            {
                using var source = new MemoryStream(new byte[] { (byte)i });
                db.FileStorage.Upload(i.ToString(), "old", source);
            }
            Assert.True(db.BeginTrans());
            LiteFileStream<string> writer;
            using (var cursor = db.FileStorage.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                writer = db.FileStorage.OpenWrite("new", "new");
                var count = 1;
                while (cursor.MoveNext()) count++;
                Assert.Equal(3, count);
            }
            using (writer) writer.WriteByte(9);
            Assert.True(db.Commit());
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("new", downloaded);
            Assert.Equal(new byte[] { 9 }, downloaded.ToArray());
        }
    }
}
