#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileDurablePrefix_Tests
    {
        [OneFileFact]
        public void Separately_captured_group_tickets_never_publish_beyond_their_covering_prefix()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var shared = new LiteDatabase(engine, disposeOnClose: false);
            shared.BeginTrans().Should().BeTrue(); // retain main + WRITE across the forced group schedule
            var core = Core(engine);
            core.Rollback().Should().BeTrue(); // keep wrapper ownership, release its empty inner transaction
            using var db = new LiteDatabase(core, disposeOnClose: false);
            db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 0, ["value"] = 0 });
            db.Checkpoint();
            var state = State(core);
            var disk = Field<DiskService>(core, "_disk");
            var table = engine.MappedReaderTable;
            var initial = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            using var firstCaptured = new ManualResetEventSlim();
            using var secondAppended = new ManualResetEventSlim();
            using var resumeFirst = new ManualResetEventSlim();
            using var secondCaptured = new ManualResetEventSlim();
            using var resumeSecond = new ManualResetEventSlim();
            using var firstPublished = new ManualResetEventSlim();
            var captures = 0;
            var barriers = 0;
            long firstVersion = -1, secondVersion = -1;
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-after-index-confirmation" && firstCaptured.IsSet) secondAppended.Set();
                if (stage == "wal-before-durable-flush")
                {
                    if (Interlocked.Increment(ref captures) == 1)
                    {
                        firstVersion = Field<WalChecksum>(disk, "_checksums").LastConfirmedPosition / Constants.PAGE_SIZE + 1;
                        firstCaptured.Set();
                        resumeFirst.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                    else
                    {
                        secondVersion = Field<WalChecksum>(disk, "_checksums").LastConfirmedPosition / Constants.PAGE_SIZE + 1;
                        secondCaptured.Set();
                        resumeSecond.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                }
                if (stage == "wal-after-durable-flush") Interlocked.Increment(ref barriers);
            };
            Action<string> observe = stage =>
            {
                if (stage != "commit-version") return;
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().BeLessThanOrEqualTo(
                    Volatile.Read(ref barriers) == 1 ? firstVersion : secondVersion);
                firstPublished.Set();
            };
            Task first = null, second = null;
            try
            {
                first = Start(() => db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 1 }), observe);
                firstCaptured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                second = Start(() => db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 2 }), observe);
                secondAppended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                resumeFirst.Set();
                secondCaptured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                firstPublished.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(firstVersion);
                disk.CommitReadVersionLimit.Should().Be((int)firstVersion);
                secondVersion.Should().BeGreaterThan(firstVersion);
                resumeSecond.Set();
                Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(10)).Should().BeTrue();
                barriers.Should().Be(2);
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(secondVersion);
            }
            finally
            {
                resumeFirst.Set(); resumeSecond.Set();
                if (first != null && second != null) Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(10));
                state.AtCrashPoint = null;
                shared.Rollback();
            }
            shared.GetCollection("group").Count().Should().Be(3);
            shared.GetCollection("group").FindById(1)["value"].AsInt32.Should().Be(1);
            shared.GetCollection("group").FindById(2)["value"].AsInt32.Should().Be(2);
            Verify(shared, 0);
            Quiet(table);
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Zero_frame_dependency_commit_cannot_publish_before_its_barrier(bool fail)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var shared = new LiteDatabase(engine, disposeOnClose: false);
            shared.BeginTrans().Should().BeTrue();
            var core = Core(engine);
            core.Rollback().Should().BeTrue(); // keep wrapper ownership, release its empty inner transaction
            using var db = new LiteDatabase(core, disposeOnClose: false);
            var disk = Field<DiskService>(core, "_disk");
            var sync = Field<object>(disk.CommitGroup, "_sync");
            var state = State(core);
            var table = engine.MappedReaderTable;
            var initial = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            using var appended = new ManualResetEventSlim();
            var hits = 0;
            var published = 0;
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-after-index-confirmation") appended.Set();
                if (stage == "wal-before-durable-flush")
                {
                    Interlocked.Increment(ref hits);
                    table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                    if (fail) throw new IOException("dependency barrier failed");
                }
            };
            Action<string> observe = stage =>
            {
                if (stage != "commit-version") return;
                hits.Should().BeGreaterThan(0);
                published++;
            };
            Task writer = null;
            Exception completion = null;
            try
            {
                lock (sync)
                {
                    writer = Start(() => db.GetCollection("docs").Delete(1).Should().BeTrue(), observe);
                    appended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    var length = disk.GetFileLength(FileOrigin.Log);
                    SharedMemoryReaderTable.PublicationStage = observe;
                    try { db.GetCollection("docs").Delete(1).Should().BeFalse(); }
                    catch (Exception error) { completion = error; }
                    finally { SharedMemoryReaderTable.PublicationStage = null; }
                    disk.GetFileLength(FileOrigin.Log).Should().Be(length, "device barriers may pad bytes but the dependency writes zero logical frames");
                    hits.Should().Be(1);
                }
                try { writer.GetAwaiter().GetResult(); } catch (IOException) when (fail) { }
                if (fail)
                {
                    completion.Should().BeAssignableTo<IOException>();
                    published.Should().Be(0);
                    table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(initial);
                }
                else { completion.Should().BeNull(); published.Should().BeGreaterThan(0); }
            }
            finally
            {
                state.AtCrashPoint = null;
                try { shared.Rollback(); } catch (IOException) when (fail) { }
            }
            if (fail) Fenced(table); else Quiet(table);
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rollback_ReturnNewPages_confirmation_is_published_only_after_the_covering_sync(bool fail)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            for (var id = 999; id < 1030; id++) db.GetCollection("abort").Insert(Row(id, 9));
            var core = Core(engine);
            var transaction = Field<TransactionMonitor>(core, "_monitor").GetTransaction(false, false, out _);
            transaction.Pages.NewPages.Should().NotBeEmpty();
            var state = State(core);
            var synced = false;
            var published = 0;
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-before-durable-flush" && fail) throw new IOException("rollback barrier failed");
                if (stage == "wal-after-durable-flush") synced = true;
            };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != "commit-version") return;
                synced.Should().BeTrue();
                published++;
            };
            try
            {
                Action rollback = () => db.Rollback().Should().BeTrue();
                if (fail) rollback.Should().Throw<IOException>(); else rollback();
                if (fail) { published.Should().Be(0); Fenced(engine.MappedReaderTable); }
                else { synced.Should().BeTrue(); published.Should().BeGreaterThan(0); Quiet(engine.MappedReaderTable); }
            }
            finally { state.AtCrashPoint = null; SharedMemoryReaderTable.PublicationStage = null; }
            for (var repeat = 0; repeat < 2; repeat++) { Verify(db, 0); db.Checkpoint(); }
        }

        private static Task Start(Action action, Action<string> observe) => Task.Factory.StartNew(() =>
        {
            SharedMemoryReaderTable.PublicationStage = observe;
            try { action(); }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
#endif
