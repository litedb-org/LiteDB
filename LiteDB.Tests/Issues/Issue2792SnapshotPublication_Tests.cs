using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2792SnapshotPublication_Tests
    {
        [Fact]
        public void Already_admitted_reader_waits_for_collection_and_wal_publication_together()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var monitor = engine.GetMonitor();
            using var readerReady = new ManualResetEventSlim();
            using var headerReached = new ManualResetEventSlim();
            using var readerStarted = new ManualResetEventSlim();
            using var releaseHeader = new ManualResetEventSlim();
            engine.SimulateDiskWriteFail = page =>
            {
                if (page.ReadUInt32(BasePage.P_PAGE_ID) != 0) return;
                headerReached.Set();
                if (!releaseHeader.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("header");
            };
            // Admission precedes the writer's engine lifecycle lock, as can happen
            // when a QueryExecutor has been created but has not opened its snapshot.
            Thread readerThread = null;
            var reader = Task.Factory.StartNew(() =>
            {
                readerThread = Thread.CurrentThread;
                var transaction = monitor.GetTransaction(true, true, out _);
                try
                {
                    readerReady.Set();
                    headerReached.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    readerStarted.Set();
                    var snapshot = transaction.CreateSnapshot(LockMode.Read, "created", false);
                    Assert.NotNull(snapshot.CollectionPage);
                }
                finally { monitor.ReleaseTransaction(transaction); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task writer = null;
            try
            {
                readerReady.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                writer = Task.Run(() => db.GetCollection("created").Insert(new BsonDocument { ["_id"] = 1 }));
                headerReached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                readerStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                // A dedicated reader thread must actually block inside snapshot
                // creation; merely signaling its intent could false-pass if descheduled.
                SpinWait.SpinUntil(() => reader.IsCompleted ||
                    (readerThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                reader.IsCompleted.Should().BeFalse("the collection page is not yet confirmed in the WAL");
            }
            finally
            {
                releaseHeader.Set();
                if (writer != null) writer.GetAwaiter().GetResult();
                reader.GetAwaiter().GetResult();
                engine.SimulateDiskWriteFail = null;
            }
            db.GetCollection("created").FindAll().Single()["_id"].AsInt32.Should().Be(1);
            db.Checkpoint();
        }
    }
}
