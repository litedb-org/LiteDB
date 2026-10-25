using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleRawEngine_Tests
    {
        [Fact]
        public void Uncaught_raw_reentry_aborts_statement_and_cannot_escape_to_autocommit()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection("rows");
                IEnumerable<BsonDocument> Input()
                {
                    yield return new BsonDocument { ["_id"] = 1 };
                    engine.Commit();
                }
                Assert.ThrowsAny<NotSupportedException>(() => rows.Insert(Input()));
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                Assert.Throws<InvalidOperationException>(() => rows.Insert(new BsonDocument { ["_id"] = 2 }));
                Assert.Throws<InvalidOperationException>(tx.Commit);
                Assert.Equal(0, db.GetCollection("rows").Count());
                db.Dispose();
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(0, cold.GetCollection("rows").Count());
        }

        public class Entity
        {
            public int Id { get; set; }
            [BsonIgnore] public Action Reading;
            public int Value { get { Reading?.Invoke(); return 42; } set { } }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Raw_engine_reentry_from_mapping_or_bulk_input_cannot_complete_the_handle(bool bulk)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection<Entity>("rows");
                rows.Insert(new Entity { Id = 1 });
                void Check()
                {
                    Assert.ThrowsAny<NotSupportedException>(() => engine.BeginTrans());
                    Assert.ThrowsAny<NotSupportedException>(() => engine.Commit());
                    Assert.ThrowsAny<NotSupportedException>(() => engine.Rollback());
                    Assert.ThrowsAny<NotSupportedException>(() => engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32));
                    Assert.ThrowsAny<NotSupportedException>(() => engine.Query("rows", new Query()));
                    Assert.ThrowsAny<NotSupportedException>(engine.Dispose);
                    // The supported public facade is explicitly independent.
                    db.GetCollection("ordinary").Upsert(new BsonDocument { ["_id"] = 1 });
                }
                IEnumerable<Entity> Input() { Check(); yield return new Entity { Id = 2 }; }
                if (bulk) rows.Insert(Input());
                else rows.Insert(new Entity { Id = 2, Reading = Check });
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.Equal(2, rows.Count());
                Assert.Equal(2, rows.UpdateMany("{ Value: 43 }", "Value = 42"));
                Assert.Equal(1, rows.DeleteMany("_id = 1"));
                Assert.Equal(1, rows.DeleteMany("Value = 43"));
                tx.Rollback();
                Assert.Equal(1, db.GetCollection("ordinary").Count());
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(0, cold.GetCollection("rows").Count());
            Assert.Equal(0, cold.GetCollection("other").Count());
            Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
            Assert.Equal(1, cold.GetCollection("ordinary").Count());
        }
    }
}
