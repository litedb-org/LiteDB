using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2891_Tests
    {
        [Fact]
        public void Reading_cols_while_another_thread_creates_a_collection_does_not_throw()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            foreach (var name in new[] { "first", "second", "third" })
            {
                db.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1 });
            }

            Exception readFailure;
            using (var reader = db.Execute("SELECT name FROM $cols"))
            {
                reader.Read().Should().BeTrue();

                // Committing a new collection mutates HeaderPage._collections while $cols still enumerates it.
                var writer = Task.Run(() => db.GetCollection("created_during_read").Insert(new BsonDocument { ["_id"] = 1 }));
                writer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                writer.Exception.Should().BeNull();

                readFailure = Record.Exception(() => { while (reader.Read()) { } });
            }

            using (new AssertionScope())
            {
                readFailure.Should().BeNull("$cols must not enumerate the live header collection dictionary (#2891)");
                db.GetCollectionNames().Should().Contain(new[] { "first", "second", "third", "created_during_read" });
            }
        }

        [Fact]
        public void Concurrent_first_inserts_into_new_collections_do_not_throw()
        {
            const int ExistingCollections = 300;
            const int NewCollectionsPerRound = 200;
            var budget = Stopwatch.StartNew();
            var failures = new ConcurrentQueue<Exception>();
            int rounds = 0;

            // The CheckName -> GetAvailableCollectionSpace race is timing dependent. Existing collections make each
            // unlocked header enumeration longer; fresh databases are repeated until it shows up or the budget is spent.
            while (failures.IsEmpty && budget.Elapsed < TimeSpan.FromSeconds(12))
            {
                rounds++;
                using var file = new TempFile();
                using var db = new LiteDatabase(file.Filename);

                db.BeginTrans().Should().BeTrue();
                for (int i = 0; i < ExistingCollections; i++)
                {
                    db.GetCollection("p_" + i).Insert(new BsonDocument { ["_id"] = i });
                }
                db.Commit().Should().BeTrue();

                Parallel.For(0, NewCollectionsPerRound, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(16, Environment.ProcessorCount * 4) }, i =>
                {
                    try { db.GetCollection("n_" + i).Insert(new BsonDocument { ["_id"] = i }); }
                    catch (Exception ex) { failures.Enqueue(ex); }
                });

                if (failures.IsEmpty)
                {
                    db.GetCollectionNames().Count().Should().Be(ExistingCollections + NewCollectionsPerRound);
                }
            }

            failures.Should().BeEmpty(
                "creating distinct collections concurrently must not race on HeaderPage._collections (#2891); " +
                "first failure after {0} round(s): {1}", rounds, failures.FirstOrDefault());
        }
    }
}
