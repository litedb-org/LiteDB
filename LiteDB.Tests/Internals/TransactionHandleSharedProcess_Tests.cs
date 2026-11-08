#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Threading.Tasks;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleSharedProcess_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public async Task Open_handle_blocks_remote_begin_until_completion_and_timeout_does_not_release_holder(bool mapped)
        {
            using var file = new TempFile();
            Seed(file);
            var mode = mapped ? "handle-mapped" : "handle-legacy";
            using (var holder = new MvccProcess(mode, file, null))
            using (var waiter = new MvccProcess(mode, file, null))
            {
                await holder.Expect("ready"); await waiter.Expect("ready");
                holder.Send("begin"); await holder.Expect("attempting"); await holder.Expect("begun");
                waiter.Send("begin"); await waiter.Expect("attempting"); await waiter.Expect("timeout");
                holder.Send("write"); await holder.Expect("written");
                holder.Send("commit"); await holder.Expect("committed");
                waiter.Send("begin"); await waiter.Expect("attempting"); await waiter.Expect("begun");
                waiter.Send("commit"); await waiter.Expect("committed");
                holder.Send("close"); waiter.Send("close");
                await holder.Expect("done"); await waiter.Expect("done");
                await holder.Finish(); await waiter.Finish();
            }
            Verify(file, true);
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public async Task Process_death_with_open_handle_discards_spill_and_recovers_preserved_indexes(bool mapped)
        {
            using var file = new TempFile(); Seed(file);
            var mode = mapped ? "handle-mapped" : "handle-legacy";
            using (var holder = new MvccProcess(mode, file, null))
            {
                await holder.Expect("ready");
                holder.Send("begin"); await holder.Expect("attempting"); await holder.Expect("begun");
                holder.Send("write"); await holder.Expect("written");
                await holder.Kill();
            }
            using (var survivor = new MvccProcess(mode, file, null))
            {
                await survivor.Expect("ready");
                survivor.Send("begin"); await survivor.Expect("attempting"); await survivor.Expect("begun");
                survivor.Send("commit"); await survivor.Expect("committed");
                survivor.Send("close"); await survivor.Expect("done"); await survivor.Finish();
            }
            Verify(file, false);
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public async Task Open_handle_excludes_file_replacement_and_rekey_without_mutation(bool mapped)
        {
            using var file = new TempFile(); Seed(file);
            using (var holder = new MvccProcess(mapped ? "handle-mapped" : "handle-legacy", file, null))
            {
                await holder.Expect("ready");
                holder.Send("begin"); await holder.Expect("attempting"); await holder.Expect("begun");
                var before = System.IO.File.ReadAllBytes(file);
                // Exclusive Direct admission is required before replacing or rekeying
                // Shared storage. It must refuse while the handle retains admission.
                Assert.ThrowsAny<Exception>(() => { using var exclusive = new LiteDatabase(file); exclusive.Rebuild(new RebuildOptions { Password = "changed" }); });
                Assert.Equal(before, System.IO.File.ReadAllBytes(file));
                holder.Send("commit"); await holder.Expect("committed");
                holder.Send("close"); await holder.Expect("done"); await holder.Finish();
            }
            Verify(file, false);
        }

        private static void Seed(string file)
        {
            using var db = new LiteDatabase(file);
            db.Timeout = TimeSpan.FromSeconds(1);
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }

        private static void Verify(string file, bool committed)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                using var db = new LiteDatabase(file);
                Assert.Equal(committed ? new[] { 1, 2 } : new[] { 1 }, db.GetCollection("rows").FindAll().Select(d => d["_id"].AsInt32).OrderBy(x => x));
                Assert.Equal(committed ? 1 : 0, db.GetCollection("rows").Count(Query.EQ("value", 20)));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }
    }
}
#endif
