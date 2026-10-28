using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using LiteDB.Vector;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Theory]
        [InlineData(5000, false)]
        [InlineData(5000, true)]
        [InlineData(20000, false)]
        [InlineData(20000, true)]
        public void Seeded_ordinary_mutations_count_deferral_without_timing(int count, bool alternateUpdates)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection<VectorDocument>("docs");
            float[] Vector(int id) => new[] { (float)(id % 97), (float)(id % 31), 1f, -1f };
            docs.Insert(Enumerable.Range(1, count).Select(id => new VectorDocument { Id = id, Embedding = Vector(id) }));
            docs.EnsureIndex("embedding_idx", "$.Embedding", new LiteDB.Vector.VectorIndexOptions(4));
            var random = new Random(91731);
            var live = new HashSet<int>(Enumerable.Range(1, count));
            var deferredWrites = 0;
            var transitions = 0;
            var first = -1;
            var previous = false;
            for (var operation = 0; operation < 1000; operation++)
            {
                var id = live.ElementAt(random.Next(live.Count));
                if (alternateUpdates && operation % 2 == 1)
                    docs.Update(new VectorDocument { Id = id, Embedding = Vector(-id) }).Should().BeTrue();
                else
                {
                    docs.Delete(id).Should().BeTrue();
                    live.Remove(id);
                }
                var deferred = RepairDeferred(db);
                if (deferred)
                {
                    deferredWrites++;
                    if (!previous) transitions++;
                    if (first < 0) first = operation + 1;
                }
                previous = deferred;
            }
            Console.WriteLine($"mutation_counts count={count} alternating={alternateUpdates} operations=1000 " +
                $"first_deferred={first} transitions={transitions} deferred_writes={deferredWrites} live={live.Count}");
            docs.FindAll().Select(x => x.Id).Should().BeEquivalentTo(live);
            // A deterministic availability guard: public bounded queries always work,
            // whether the current execution mode is approximate or exact.
            docs.Query().TopKNear(x => x.Embedding, Vector(1), 10).ToArray().Should().HaveCount(10);
            deferredWrites.Should().Be(0, "these ordinary seeded mutations must fit local repair");
        }
    }
}
