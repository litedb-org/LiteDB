using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointReader_Tests
    {
        [Theory]
        [InlineData("cursor", null)]
        [InlineData("include", null)]
        [InlineData("borrowed", null)]
        [InlineData("cursor", "secret")]
        [InlineData("include", "secret")]
        [InlineData("borrowed", "secret")]
        public async Task OldGenerationSurvivesPressurePartialPassesAndForeignCursorDisposal(string shape, string password)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, DurableCommits = false,
                CompactStorage = CompactStorageMode.Legacy, CacheSize = 65536, TransactionPageLimit = 2
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.Timeout = TimeSpan.FromSeconds(1);
            db.GetCollection("owners").Insert(Owner(-1));
            db.GetCollection("rows").Insert(Enumerable.Range(0, 40).Select(id => Row(id, -1)));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            db.CheckpointSize = 32;
            db.Checkpoint();
            var worker = engine.InstallDirectCheckpointLifetime();
            var disk = BackgroundCheckpointIntegrationScenario.DiskOf(engine);
            using var opened = new ManualResetEventSlim();
            using var inspect = new ManualResetEventSlim();
            using var inspected = new ManualResetEventSlim();
            using var releaseReader = new ManualResetEventSlim();
            using var pass = new ManualResetEventSlim();
            using var setupPause = worker.Pause();
            // Pin a generation that exists in WAL, distinct from the data-file
            // predecessor. Reclaiming its frames would invalidate continuation.
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("rows").Update(Enumerable.Range(0, 40).Select(id => Row(id, 0))).Should().Be(40);
            db.GetCollection("owners").Update(Owner(0)).Should().BeTrue();
            db.Commit().Should().BeTrue();
            disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0);
            disk.WriteAdmission.UnderPressure.Should().BeFalse();
            IEnumerator<BsonDocument> cursor = null;
            BsonDocument retained = null;
            var reader = DirectCheckpointLifetime_Tests.Start(() =>
            {
                var query = db.GetCollection("rows").Query();
                if (shape == "include") query = query.Include("Ref");
                if (shape == "borrowed") query = query.Where("Nested.Value >= 0");
                BorrowedQueryDiagnostics.Reset();
                cursor = query.ToEnumerable().GetEnumerator();
                cursor.MoveNext().Should().BeTrue();
                retained = cursor.Current;
                if (shape == "borrowed") BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().BeGreaterThan(0);
                opened.Set();
                AutomaticCheckpointWorker_Tests.Wait(inspect);
                var old = new List<BsonDocument> { retained };
                for (var i = 1; i < 40; i++)
                {
                    cursor.MoveNext().Should().BeTrue();
                    old.Add(cursor.Current);
                }
                old.Should().BeEquivalentTo(Enumerable.Range(0, 40).Select(id => Expected(id, 0, shape)));
                inspected.Set();
                AutomaticCheckpointWorker_Tests.Wait(releaseReader);
            });
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(opened);
                long clock = 0;
                worker.Backoff.Timestamp = () => Interlocked.Read(ref clock);
                worker.AfterCheckpointWorkerPass = pass.Set;
                using (worker.Pause())
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").Update(Enumerable.Range(0, 40).Select(id => Row(id, 1))).Should().Be(40);
                    db.GetCollection("owners").Update(Owner(1)).Should().BeTrue();
                    db.Commit().Should().BeTrue();
                    disk.WriteAdmission.UnderPressure.Should().BeTrue();
                }
                setupPause.Dispose();
                var delays = new[] { 50, 100, 200, 400 };
                for (var attempt = 0; attempt < delays.Length; attempt++)
                {
                    AutomaticCheckpointWorker_Tests.Wait(pass);
                    using (worker.Pause())
                    {
                        worker.Backoff.RemainingMilliseconds.Should().Be(delays[attempt]);
                        disk.WriteAdmission.UnderPressure.Should().BeTrue("partial reuse is not tail reclamation");
                        disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThanOrEqualTo(128L * Constants.PAGE_SIZE);
                        ((object)retained).Should().BeEquivalentTo(Expected(0, 0, shape));
                        for (var request = 0; request < 20; request++) worker.Request(worker.Incarnation);
                        worker.Backoff.RemainingMilliseconds.Should().Be(delays[attempt], "requests cannot postpone or bypass reader backoff");
                        pass.Reset();
                        if (attempt + 1 < delays.Length)
                        {
                            Interlocked.Add(ref clock, delays[attempt] * Stopwatch.Frequency / 1000 + 1);
                            worker.PublishTail(disk.GetFileLength(FileOrigin.Log));
                        }
                    }
                }
                var before = disk.GetFileLength(FileOrigin.Log);
                var timeout = DirectCheckpointLifetime_Tests.Start(() => db.GetCollection("rows").Insert(Row(99, 1)));
                await DirectCheckpointLifetime_Tests.Completes(timeout);
                timeout.Result.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
                disk.GetFileLength(FileOrigin.Log).Should().Be(before);
                inspect.Set();
                AutomaticCheckpointWorker_Tests.Wait(inspected);
                // Dispose on a thread other than the cursor's opening thread.
                // The saved document must still own every byte after page unpin.
                using (worker.Pause())
                {
                    cursor.Dispose();
                    cursor = null;
                    worker.Backoff.RemainingMilliseconds.Should().Be(0);
                    disk.GetFileLength(FileOrigin.Log).Should().Be(before, "unpin only signals, it performs no I/O");
                    pass.Reset();
                }
                AutomaticCheckpointWorker_Tests.Wait(pass);
                db.Checkpoint();
                disk.WriteAdmission.UnderPressure.Should().BeFalse();
                ((object)retained).Should().BeEquivalentTo(Expected(0, 0, shape));
                // The cursor was disposed early; a fresh reader now sees the new generation.
                Verify(db);
            }
            finally
            {
                setupPause.Dispose();
                inspect.Set();
                releaseReader.Set();
                await DirectCheckpointLifetime_Tests.Completes(reader);
                cursor?.Dispose();
            }
            reader.Result.Should().BeNull();
            engine.Dispose();
            for (var open = 0; open < 3; open++)
            {
                using (var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password }))
                {
                    Verify(reopened);
                    reopened.Checkpoint();
                }
                AllocationReachability.Verify(file.Filename, password);
            }
        }

        private static BsonDocument Row(int id, int generation) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id * 7 + generation * 1000,
            ["Nested"] = new BsonDocument { ["Value"] = id },
            ["payload"] = new string((char)('a' + id % 20 + generation), 12000),
            ["Ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" }
        };

        private static BsonDocument Owner(int generation) => new BsonDocument
        { ["_id"] = 1, ["Name"] = "owner-" + generation, ["payload"] = new string((char)('x' + generation), 9000) };

        private static BsonDocument Expected(int id, int generation, string shape)
        {
            var result = Row(id, generation);
            if (shape == "include")
            {
                var owner = Owner(generation);
                owner["$id"] = 1;
                result["Ref"] = owner;
            }
            return result;
        }

        private static void Verify(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");
            rows.FindAll().Should().BeEquivalentTo(Enumerable.Range(0, 40).Select(id => Row(id, 1)));
            rows.Query().Include("Ref").ToArray().Should().BeEquivalentTo(
                Enumerable.Range(0, 40).Select(id => Expected(id, 1, "include")));
            foreach (var id in Enumerable.Range(0, 40))
            {
                rows.Find(Query.EQ("value", id * 7)).Should().BeEmpty();
                rows.Find(Query.EQ("value", id * 7 + 1000)).Should().BeEquivalentTo(new[] { Row(id, 1) });
            }
            ((object)db.GetCollection("owners").FindById(1)).Should().BeEquivalentTo(Owner(1));
            db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
        }
    }
}
