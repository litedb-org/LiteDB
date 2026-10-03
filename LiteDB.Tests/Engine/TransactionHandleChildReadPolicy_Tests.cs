using System;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleChildReadPolicy_Tests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Replacing_callers_settings_delegate_does_not_change_constructed_shared_connection(bool initial, bool replacement)
        {
            using var file = new TempFile();
            var oldCalls = 0;
            var newCalls = 0;
            var settings = new EngineSettings
            {
                Filename = file,
                ReadTransform = initial ? (Func<string, BsonValue, BsonValue>)((_, value) =>
                { oldCalls++; value.AsDocument["policy"] = "original"; return value; }) : null
            };
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            {
                using (var warm = db.BeginTransaction())
                {
                    warm.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["policy"] = "stored" });
                    Assert.Equal(initial ? "original" : "stored", warm.GetCollection("rows").FindById(1)["policy"].AsString);
                    warm.Commit();
                }
                settings.ReadTransform = replacement ? (Func<string, BsonValue, BsonValue>)((_, value) =>
                { newCalls++; value.AsDocument["policy"] = "replacement"; return value; }) : null;
                Assert.Equal(initial ? "original" : "stored", db.GetCollection("rows").FindById(1)["policy"].AsString);
                using (var next = db.BeginTransaction())
                {
                    Assert.Equal(initial ? "original" : "stored", next.GetCollection("rows").FindById(1)["policy"].AsString);
                    next.Commit();
                }
                Assert.Equal(initial ? 3 : 0, oldCalls);
                Assert.Equal(0, newCalls);
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal("stored", cold.GetCollection("rows").FindById(1)["policy"].AsString);
        }

        [Fact]
        public void Later_handle_observes_changes_to_the_original_delegate_target()
        {
            using var file = new TempFile();
            var policy = "first";
            using var shared = new SharedEngine(new EngineSettings
            {
                Filename = file,
                ReadTransform = (_, value) => { value.AsDocument["policy"] = policy; return value; }
            });
            using var db = new LiteDatabase(shared, disposeOnClose: false);
            using (var warm = db.BeginTransaction())
            {
                warm.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.Equal("first", warm.GetCollection("rows").FindById(1)["policy"].AsString);
                warm.Commit();
            }
            policy = "second";
            Assert.Equal("second", db.GetCollection("rows").FindById(1)["policy"].AsString);
            using var next = db.BeginTransaction();
            Assert.Equal("second", next.GetCollection("rows").FindById(1)["policy"].AsString);
            next.Commit();
        }
    }
}
