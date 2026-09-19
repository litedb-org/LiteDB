using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344SharedReader_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Delete_preserves_the_shared_engine_borrowed_from_a_reader(bool existingFile)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            if (existingFile)
            {
                using var source = new MemoryStream(new byte[] { 1, 2 });
                db.FileStorage.Upload("target", "file", source);
            }
            using (var cursor = rows.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.Equal(existingFile, db.FileStorage.Delete("target"));
                Assert.True(cursor.MoveNext());
                Assert.False(cursor.MoveNext());
            }
            Assert.False(db.FileStorage.Exists("target"));
            using var direct = new LiteDatabase(file.Filename);
            Assert.Equal(2, direct.GetCollection("rows").Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Borrowed_transaction_transfers_engine_ownership_when_reader_closes_first(bool rollback)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            using (var cursor = rows.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.True(db.BeginTrans());
                rows.Insert(new BsonDocument { ["_id"] = 3 });
            }
            if (rollback) Assert.True(db.Rollback());
            else Assert.True(db.Commit());
            using var direct = new LiteDatabase(file.Filename);
            Assert.Equal(rollback ? 2 : 3, direct.GetCollection("rows").Count());
        }
    }
}
