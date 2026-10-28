using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Fact]
        public void Review2_hnswd_census_accepts_foreign_pages_committed_after_writer_snapshot()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                db.CheckpointSize = 0;
                db.BeginTrans().Should().BeTrue();
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        return true;
                    })
                });
                Task.Run(() => db.GetCollection("foreign").Insert(new BsonDocument
                    { ["_id"] = 1, ["payload"] = new string('x', 40000) })).GetAwaiter().GetResult();
                Action delete = () => db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue();
                delete.Should().NotThrow("a commit to another collection must not invalidate vector deletion");
                db.Commit().Should().BeTrue();
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('x', 40000));
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(64);
                AssertAllocatedVectorLinks(db, 64);
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Length.Should().Be(40000);
            }
        }

        public static IEnumerable<object[]> CensusInterleavings()
        {
            foreach (var operation in new[] { "delete", "drop", "repair" })
            foreach (var reuse in new[] { false, true })
            foreach (var foreignRollback in new[] { false, true })
            foreach (var writerRollback in new[] { false, true })
                yield return new object[] { operation, reuse, foreignRollback, writerRollback };
        }

        [Theory]
        [MemberData(nameof(CensusInterleavings))]
        public void Vector_census_preserves_foreign_commits_and_writer_atomicity(
            string operation, bool reuse, bool foreignRollback, bool writerRollback)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                BuildBoundedRepairGraph(db, 65, arms: false);
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                if (reuse) InsertCensusForeign(db, "retired");
                if (operation == "repair") SetCensusDeferred(db);
                db.Checkpoint();
                var retiredPages = reuse ? CensusCollectionPages(db, "retired") : new uint[0];
                db.BeginTrans().Should().BeTrue();
                CensusSnapshot(db, LockMode.Write, snapshot => snapshot.ReadVersion);
                Task.Run(() =>
                {
                    if (reuse) db.DropCollection("retired").Should().BeTrue();
                    db.BeginTrans().Should().BeTrue();
                    InsertCensusForeign(db, "foreign");
                    if (reuse)
                    {
                        // Check actual physical reuse, before either outcome of
                        // the foreign transaction, rather than assuming allocation.
                        CensusPrivatePages(db, "foreign").Intersect(retiredPages).Should().NotBeEmpty();
                    }
                    if (foreignRollback) db.Rollback().Should().BeTrue();
                    else db.Commit().Should().BeTrue();
                }).GetAwaiter().GetResult();
                var docs = db.GetCollection<VectorDocument>("docs");
                if (operation == "drop") docs.DropIndex("embedding_idx").Should().BeTrue();
                else docs.Delete(2).Should().BeTrue();
                if (operation == "repair") RepairDeferred(db).Should().BeFalse("the small graph must actually reconcile");
                if (writerRollback) db.Rollback().Should().BeTrue();
                else db.Commit().Should().BeTrue();
                AssertCensusOutcome(db, operation, writerRollback, foreignRollback);
                // Leave commits in WAL; reopen must recover both collections.
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                AssertCensusOutcome(db, operation, writerRollback, foreignRollback);
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
                AssertCensusOutcome(db, operation, writerRollback, foreignRollback);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Vector_read_census_keeps_pinned_pages_after_foreign_growth_and_reuse(bool reuse, bool foreignRollback)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                BuildBoundedRepairGraph(db, 65, arms: false);
                // Keep the original graph WAL-only too, so newest images cannot
                // accidentally substitute for the reader's pinned images.
                db.BeginTrans().Should().BeTrue();
                var original = CensusSnapshot(db, LockMode.Read, snapshot => snapshot.GetVectorPages());
                original.Should().NotBeEmpty();
                using var ready = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var peer = Task.Run(() =>
                {
                    if (reuse) db.GetCollection<VectorDocument>("docs").DropIndex("embedding_idx").Should().BeTrue();
                    db.BeginTrans().Should().BeTrue();
                    InsertCensusForeign(db, "foreign");
                    if (reuse) CensusPrivatePages(db, "foreign").Intersect(original).Should().NotBeEmpty();
                    ready.Set();
                    release.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                    if (foreignRollback) db.Rollback().Should().BeTrue();
                    else db.Commit().Should().BeTrue();
                });
                try
                {
                    ready.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                    CensusSnapshot(db, LockMode.Read, snapshot => snapshot.GetVectorPages()).Should().Equal(original,
                        "a foreign reservation cannot hide a page visible to the reader's pin");
                }
                finally { release.Set(); peer.GetAwaiter().GetResult(); }
                CensusSnapshot(db, LockMode.Read, snapshot => snapshot.GetVectorPages()).Should().Equal(original);
                // A collection read must ignore this transaction's private images
                // too, even when those pages are beyond the data-file end.
                InsertCensusForeign(db, "private");
                AutoTransactionMethod.MakeGenericMethod(typeof(int)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, int>(transaction => (int)typeof(TransactionService)
                        .GetMethod("PersistDirtyPages", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(transaction, new object[] { false }))
                });
                CensusSnapshot(db, LockMode.Read, snapshot => snapshot.GetVectorPages()).Should().Equal(original);
                db.Rollback().Should().BeTrue();
                db.GetCollection("foreign").Count().Should().Be(foreignRollback ? 0 : 1);
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("foreign").Count().Should().Be(foreignRollback ? 0 : 1);
                db.GetCollection("private").Count().Should().Be(0);
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(65);
            }
        }

        private static T CensusSnapshot<T>(LiteDatabase db, LockMode mode, Func<Snapshot, T> inspect) =>
            (T)AutoTransactionMethod.MakeGenericMethod(typeof(T)).Invoke(EngineField.GetValue(db), new object[]
            {
                new Func<TransactionService, T>(transaction => inspect(transaction.CreateSnapshot(mode, "docs", false)))
            });

        private static void SetCensusDeferred(LiteDatabase db) => CensusSnapshot(db, LockMode.Write, snapshot =>
        {
            snapshot.CollectionPage.GetVectorIndexes().Single().Index.VectorRepairDeferred = true;
            snapshot.CollectionPage.IsDirty = true;
            return true;
        });

        private static uint[] CensusCollectionPages(LiteDatabase db, string collection) => db.Execute(
            "SELECT $ FROM $dump WHERE $.collection = @0", collection).ToArray().Select(x => (uint)x["pageID"].AsInt32).ToArray();

        private static uint[] CensusPrivatePages(LiteDatabase db, string collection) =>
            (uint[])AutoTransactionMethod.MakeGenericMethod(typeof(uint[])).Invoke(EngineField.GetValue(db), new object[]
            {
                new Func<TransactionService, uint[]>(transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                    return snapshot.LocalPages.Select(x => x.PageID).Concat(new[] { snapshot.CollectionPage.PageID }).ToArray();
                })
            });

        private static void InsertCensusForeign(LiteDatabase db, string collection)
        {
            var docs = db.GetCollection(collection);
            docs.Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('x', 40000) });
        }

        private static void AssertCensusOutcome(LiteDatabase db, string operation, bool writerRollback, bool foreignRollback)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            var count = writerRollback || operation == "drop" ? 65 : 64;
            docs.Count().Should().Be(count);
            (docs.FindById(2) != null).Should().Be(count == 65);
            var dropped = operation == "drop" && !writerRollback;
            db.Execute("SELECT $ FROM $indexes WHERE collection = 'docs'").ToArray().Any(x => x["name"].AsString == "embedding_idx").Should().Be(!dropped);
            if (dropped) AllocatedVectorPages(db, "docs").Should().Be(0);
            else
            {
                RepairDeferred(db).Should().Be(operation == "repair" && writerRollback);
                AssertAllocatedVectorLinks(db, count);
                AssertConnectivity(db, count);
                docs.Query().TopKNear(x => x.Embedding, BoundedVector(1), count).ToArray()
                    .Select(x => x.Id).Should().BeEquivalentTo(docs.FindAll().Select(x => x.Id));
            }
            db.GetCollection("foreign").Count().Should().Be(foreignRollback ? 0 : 1);
            if (!foreignRollback) db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('x', 40000));
            db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
        }
    }
}
