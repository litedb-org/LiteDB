#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitReviewLifecycle_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Rebuild_waits_for_pending_admission_and_dispose_fails_pending_writes(bool dispose)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 0 });
            db.Checkpoint();
            using var barrier = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var exclusiveAttempt = new ManualResetEventSlim();
            engine.SimulateBeforeExclusiveAdmission = () => exclusiveAttempt.Set();
            var syncs = 0;
            var acknowledgements = 0;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase != "wal-before-durable-flush" || Interlocked.Increment(ref syncs) != 1) return;
                barrier.Set();
                release.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
            };
            var writer = Task.Factory.StartNew<Exception>(() =>
            {
                try
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                    Interlocked.Increment(ref acknowledgements);
                    return null;
                }
                catch (Exception error) { return error; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task maintenance = null;
            try
            {
                barrier.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                acknowledgements.Should().Be(0);
                maintenance = Task.Factory.StartNew(() =>
                {
                    if (dispose) engine.Dispose();
                    else engine.Rebuild(new RebuildOptions());
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (dispose) await Completes(maintenance);
                else
                {
                    exclusiveAttempt.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    (await Task.WhenAny(maintenance, Task.Delay(100))).Should().NotBeSameAs(maintenance);
                }
                release.Set();
                await Completes(writer);
                await Completes(maintenance);
                if (dispose)
                {
                    (await writer).Should().BeAssignableTo<IOException>();
                    acknowledgements.Should().Be(0);
                }
                else
                {
                    (await writer).Should().BeNull();
                    acknowledgements.Should().Be(1);
                    db.GetCollection("rows").Count().Should().Be(2);
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                    db.Checkpoint();
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
                    db.GetCollection("rows").Count().Should().Be(4);
                }
            }
            finally
            {
                release.Set();
                try
                {
                    await Completes(writer);
                    if (maintenance != null) await Completes(maintenance);
                }
                finally { EngineState.SimulateProcessCrash = null; }
            }
        }

        private static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(task);
            await task;
        }
    }
}
#endif
