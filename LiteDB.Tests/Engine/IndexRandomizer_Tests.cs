using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexRandomizer_Tests
    {
        [Fact]
        public void Seeded_levels_follow_geometric_distribution()
        {
            const int count = 131072;
            var random = Randomizer.WithSeed(3131);
            var repeat = Randomizer.WithSeed(3131);
            var buckets = new int[12];
            for (var i = 0; i < count; i++)
            {
                var level = IndexService.Flip(random);
                Assert.Equal(level, IndexService.Flip(repeat));
                Assert.InRange(level, (byte)1, (byte)Constants.MAX_LEVEL_LENGTH);
                buckets[Math.Min(level - 1, buckets.Length - 1)]++;
            }

            double chiSquare = 0;
            for (var i = 0; i < buckets.Length; i++)
            {
                // The last bucket includes the entire upper tail.
                var expected = count / Math.Pow(2, i == buckets.Length - 1 ? i : i + 1);
                chiSquare += Math.Pow(buckets[i] - expected, 2) / expected;
            }
            // 11 degrees of freedom; a deliberately conservative deterministic guard.
            Assert.InRange(chiSquare, 0, 40);
        }

        [Theory]
        [InlineData(0x61C8864680B583EBUL, 0U, 1)]
        [InlineData(0x25114ED53327345AUL, uint.MaxValue, 32)]
        public void Extreme_coin_words_reach_both_height_bounds(ulong seed, uint word, int level)
        {
            Assert.Equal(word, Randomizer.WithSeed(seed).Next());
            Assert.Equal(level, (int)IndexService.Flip(Randomizer.WithSeed(seed)));
        }

        [Fact]
        public void Simultaneously_created_threads_have_independent_sources()
        {
            var sources = new Randomizer[8];
            var sequences = new string[8];
            using var barrier = new Barrier(8);
            var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                sources[i] = Randomizer.Current;
                Assert.Same(sources[i], Randomizer.Current);
                sequences[i] = string.Join(",", Enumerable.Range(0, 32).Select(_ => sources[i].Next()));
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            Assert.Equal(8, sources.Distinct().Count());
            Assert.Equal(8, sequences.Distinct().Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Concurrent_engines_preserve_indexes_through_rollback_and_reopen(bool checkpoint)
        {
            using var barrier = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
            {
                using var file = new TempFile();
                barrier.SignalAndWait();
                using (var db = new LiteDatabase(file.Filename))
                {
                    var col = db.GetCollection("docs");
                    col.EnsureIndex("key", "Key", true);
                    col.EnsureIndex("tags", "Tags[*]");
                    col.InsertBulk(Enumerable.Range(0, 256).Reverse().Select(id => new BsonDocument
                    {
                        ["_id"] = id, ["Key"] = 1000 + id,
                        ["Tags"] = new BsonArray(id % 7, (id + 1) % 7)
                    }));
                    Assert.Throws<LiteException>(() => col.Insert(new BsonDocument
                    {
                        ["_id"] = 999, ["Key"] = 1000, ["Tags"] = new BsonArray(99)
                    }));
                    db.BeginTrans();
                    col.Delete(0);
                    col.Update(new BsonDocument { ["_id"] = 1, ["Key"] = -1, ["Tags"] = new BsonArray(99) });
                    db.Rollback();
                    Verify(col);
                    if (checkpoint) db.Checkpoint();
                }
                using var reopened = new LiteDatabase(file.Filename);
                var collection = reopened.GetCollection("docs");
                Verify(collection);
                // Continue writing into the persisted randomly shaped lists.
                collection.Delete(128);
                collection.Update(new BsonDocument { ["_id"] = 127, ["Key"] = -1, ["Tags"] = new BsonArray(99) });
                Assert.Equal(127, collection.FindOne(Query.EQ("Key", -1))["_id"].AsInt32);
                Assert.Null(collection.FindOne(Query.EQ("Key", 1128)));
                Assert.Equal(127, collection.FindOne(Query.EQ("Tags", 99))["_id"].AsInt32);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(90)));
        }

        private static void Verify(ILiteCollection<BsonDocument> col)
        {
            Assert.Equal(256, col.Count());
            Assert.Equal(Enumerable.Range(0, 256), col.Find(Query.All("Key", Query.Ascending)).Select(d => d["_id"].AsInt32));
            Assert.Equal(Enumerable.Range(0, 256).Reverse(), col.Find(Query.All("Key", Query.Descending)).Select(d => d["_id"].AsInt32));
            for (var tag = 0; tag < 7; tag++)
            {
                var expected = Enumerable.Range(0, 256).Where(id => id % 7 == tag || (id + 1) % 7 == tag);
                Assert.Equal(expected, col.Find(Query.EQ("Tags", tag)).Select(d => d["_id"].AsInt32).OrderBy(id => id));
            }
            for (var id = 0; id < 256; id++)
                Assert.Equal(id, col.FindOne(Query.EQ("Key", 1000 + id))["_id"].AsInt32);
        }
    }
}
