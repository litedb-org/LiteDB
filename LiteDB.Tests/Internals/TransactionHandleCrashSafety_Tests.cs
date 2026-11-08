#if NET8_0_OR_GREATER
using System.Linq;
using System.Threading.Tasks;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class TransactionHandleCrashSafety_Tests
    {
        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public async Task Direct_process_death_discards_uncommitted_spill_and_releases_native_admission(string password)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Password = password };
            using (var seed = new LiteDatabase(settings))
            {
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9, ["value"] = "untouched" });
                seed.Checkpoint();
            }
            using (var child = new MvccProcess("handle-direct", file, password))
            {
                await child.Expect("ready");
                child.Send("begin"); await child.Expect("attempting"); await child.Expect("begun");
                // The child refuses this handshake unless uncommitted WAL length grows.
                child.Send("write"); await child.Expect("written");
                await child.Kill();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(settings);
                var rows = cold.GetCollection("rows");
                Assert.Equal(new[] { 1 }, rows.FindAll().Select(row => row["_id"].AsInt32));
                Assert.Equal(1, rows.Count(Query.EQ("value", 10)));
                Assert.Equal(0, rows.Count(Query.EQ("value", 20)));
                Assert.Equal("value", rows.Query().Where(Query.EQ("value", 10)).GetPlan()["index"]["name"].AsString);
                Assert.Equal("untouched", cold.GetCollection("sentinel").FindById(9)["value"].AsString);
                using var next = cold.BeginTransaction();
                rows = next.GetCollection("probe");
                rows.Insert(new BsonDocument { ["_id"] = pass });
                next.Rollback();
            }
        }
    }
}
#endif
