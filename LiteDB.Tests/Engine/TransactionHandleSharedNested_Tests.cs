using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedNested_Tests
    {
#pragma warning disable CS0618
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Handle_begin_rejects_callers_locking_reader_or_pinned_legacy_transaction(bool legacy)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(0, 150).Select(i => new BsonDocument { ["_id"] = i }));
            using (var reader = db.Execute(legacy ? "SELECT $ FROM rows" : "SELECT $ FROM rows FOR UPDATE"))
            {
                Assert.True(reader.Read());
                if (legacy)
                {
                    rows.Insert(new BsonDocument { ["_id"] = 200 });
                    Assert.True(db.BeginTrans());
                    rows.Insert(new BsonDocument { ["_id"] = 201 });
                }
                // Ownership must be rejected before waiting.
                Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
                Assert.True(reader.Read());
                if (legacy) Assert.True(db.Commit());
            }
            using var tx = db.BeginTransaction();
            Assert.Equal(legacy ? 152 : 150, tx.GetCollection("rows").Count());
            tx.Commit();
        }
#pragma warning restore CS0618
    }
}
