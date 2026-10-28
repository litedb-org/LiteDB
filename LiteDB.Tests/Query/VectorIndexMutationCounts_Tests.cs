using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;

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
            using var levels = new MutationLevelScope();
            using var file = new TempFile();
            if (count == 20000)
            {
                // A genuine old-writer checkpoint avoids repeating quadratic
                // index construction; every mutation and oracle below is unchanged.
                using var resource = File.OpenRead(ArtifactFixtures.Path("HnswMutation20000_6b2ecd3af.zip"));
                using var archive = new ZipArchive(resource);
                using var input = archive.GetEntry("vectors.db").Open();
                using var output = File.Create(file.Filename);
                input.CopyTo(output);
            }
            using var db = new LiteDatabase(count == 20000 ? file.Filename : ":memory:");
            var docs = db.GetCollection<VectorDocument>("docs");
            float[] Vector(int id) => new[] { (float)(id % 97), (float)(id % 31), 1f, -1f };
            if (count == 5000)
            {
                docs.Insert(Enumerable.Range(1, count).Select(id => new VectorDocument { Id = id, Embedding = Vector(id) }));
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(4));
            }
            docs.Count().Should().Be(count);
            RepairDeferred(db).Should().BeFalse();
            var random = new Random(91731);
            var live = new HashSet<int>(Enumerable.Range(1, count));
            var updated = new HashSet<int>();
            var deferredWrites = 0;
            var transitions = 0;
            var first = -1;
            var previous = false;
            for (var operation = 0; operation < 1000; operation++)
            {
                var id = live.ElementAt(random.Next(live.Count));
                if (alternateUpdates && operation % 2 == 1)
                {
                    docs.Update(new VectorDocument { Id = id, Embedding = Vector(-id) }).Should().BeTrue();
                    updated.Add(id);
                }
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
            var stored = docs.FindAll().ToArray();
            stored.Select(x => x.Id).Should().BeEquivalentTo(live);
            foreach (var document in stored)
                document.Embedding.Should().Equal(Vector(updated.Contains(document.Id) ? -document.Id : document.Id));
            // A deterministic availability guard: public bounded queries always work,
            // whether the current execution mode is approximate or exact.
            docs.Query().TopKNear(x => x.Embedding, Vector(1), 10).ToArray().Should().HaveCount(10);
            deferredWrites.Should().Be(0, "these ordinary seeded mutations must fit local repair");
        }
        private sealed class MutationLevelScope : IDisposable
        {
            private readonly Func<Random> _previous = VectorIndexService.LevelRandomFactory;
            internal MutationLevelScope() { VectorIndexService.LevelRandomFactory = () => new Random(91731); }
            public void Dispose() { VectorIndexService.LevelRandomFactory = _previous; }
        }
    }
}
