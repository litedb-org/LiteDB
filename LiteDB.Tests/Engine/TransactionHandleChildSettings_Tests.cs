using System;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleChildSettings_Tests
    {
        [Theory]
        [InlineData(null, "new-secret", false)]
        [InlineData("old-secret", "new-secret", false)]
        [InlineData("old-secret", null, false)]
        [InlineData(null, null, true)]
        public void Handle_after_rebuild_uses_changed_password_or_collation(string before, string after, bool changeCollation)
        {
            using var file = new TempFile();
            var collation = new Collation("en-US/IgnoreCase");
            using (var shared = new SharedEngine(new EngineSettings { Filename = file, Password = before, Collation = collation }))
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            {
                using (var warm = db.BeginTransaction())
                {
                    warm.GetCollection("rows").EnsureIndex("word");
                    warm.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["word"] = "kept" });
                    warm.Commit();
                }
                db.Rebuild(new RebuildOptions { Password = after, RemovePassword = before != null && after == null,
                    Collation = changeCollation ? new Collation("en-US/None") : null });
                using var next = db.BeginTransaction();
                Assert.NotNull(next.GetCollection("rows").FindOne("word = 'kept'"));
                next.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["word"] = "after rebuild" });
                next.Commit();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = after });
                Assert.Equal(changeCollation ? "en-US/None" : "en-US/IgnoreCase", cold.Collation.ToString());
                Assert.Equal(2, cold.GetCollection("rows").Count());
                Assert.NotNull(cold.GetCollection("rows").FindOne("word = 'kept'"));
                Assert.NotNull(cold.GetCollection("rows").FindOne("word = 'after rebuild'"));
            }
        }

        [Fact]
        public void Changed_serialized_collation_policy_is_observed_by_later_handle()
        {
            using var file = new TempFile();
            var policy = new MutableCollation();
            using var shared = new SharedEngine(new EngineSettings { Filename = file, Collation = policy });
            using var db = new LiteDatabase(shared, disposeOnClose: false);
            using (var warm = db.BeginTransaction())
            {
                warm.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                warm.Commit();
            }
            policy.Value = "en-US/None";
            try
            {
                Assert.Throws<LiteException>(() => { using var invalid = db.BeginTransaction(); });
            }
            finally { policy.Value = "en-US/IgnoreCase"; }
            using var retry = db.BeginTransaction();
            Assert.NotNull(retry.GetCollection("rows").FindById(1));
            retry.Commit();
        }

        private sealed class MutableCollation : Collation
        {
            internal string Value = "en-US/IgnoreCase";
            internal MutableCollation() : base("en-US/IgnoreCase") { }
            public override string ToString() => Value;
        }
    }
}
