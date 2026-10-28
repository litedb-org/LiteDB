using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(nameof(SharedAbandonedReaderCollection))]
    public class SharedReaderCleanupProgress_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Blocked_lease_release_does_not_stall_another_abandoned_readers_pins(bool releaseFails)
        {
            using var file = new TempFile();
            SharedAbandonedReader_Tests.Seed(file.Filename);
            using var blocked = new SharedEngine(new EngineSettings { Filename = file.Filename });
            using var other = new SharedEngine(new EngineSettings { Filename = file.Filename });
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            using var timedOut = new ManualResetEventSlim();
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);

            // Pause only dispatch so both real wrapper finalizers publish into the
            // same batch. Otherwise the old serial drain could pass by taking two batches.
            var reaper = (Timer)Field(typeof(SharedReaderCleanup), "Reaper").GetValue(null);
            reaper.Change(Timeout.Infinite, Timeout.Infinite);
            Thread drain = null;
            Exception drainError = null;
            try
            {
                var otherResult = SharedAbandonedReader_Tests.Abandon(other);
                SharedAbandonedReader_Tests.Collect();
                otherResult.IsAlive.Should().BeFalse();
                ReaderCount(other).Should().Be(1);

                var blockedResult = SharedAbandonedReader_Tests.Abandon(blocked);
                SharedAbandonedReader_Tests.Collect();
                blockedResult.IsAlive.Should().BeFalse();
                ReaderCount(blocked).Should().Be(1);
                var registry = Field(typeof(SharedEngine), "_readers").GetValue(blocked);
                var slots = (SharedReaderSlots)Field(typeof(SharedReaderRegistry), "_slots").GetValue(registry);
                slots.WriteOverride = (stream, bytes) =>
                {
                    entered.Set();
                    try
                    {
                        if (!resume.Wait(TimeSpan.FromSeconds(60)))
                        {
                            timedOut.Set();
                            throw new TimeoutException("The test did not resume the slot release.");
                        }
                        if (releaseFails) throw new IOException("Injected lease release failure.");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    finally { finished.Set(); }
                };

                var head = Field(typeof(SharedReaderCleanup), "_pending").GetValue(null);
                var next = Field(typeof(SharedReaderCleanup), "_next");
                head.Should().NotBeNull();
                next.GetValue(head).Should().NotBeNull("both results must be in one pending batch");
                next.GetValue(next.GetValue(head)).Should().BeNull();

                // The list is LIFO: the blocked reader was collected last.
                drain = new Thread(() =>
                {
                    try { SharedReaderCleanup.Drain(); }
                    catch (Exception ex) { drainError = ex; }
                }) { IsBackground = true };
                drain.Start();
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the first owner must reach slot release under its gate");
                SpinWait.SpinUntil(() => ReaderCount(other) == 0, TimeSpan.FromSeconds(5)).Should()
                    .BeTrue("the second abandoned result must release its cursor and registration while the first is blocked");
                ReaderCount(blocked).Should().Be(1);
                finished.IsSet.Should().BeFalse();
                SharedAbandonedReader_Tests.Collect();
                Volatile.Read(ref finalized).Should().Be(0, "both resource graphs must be disposed before their pinned pages can finalize");
            }
            finally
            {
                resume.Set();
                reaper.Change(20, 20);
                try
                {
                    if (drain != null)
                    {
                        drain.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        finished.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                    SpinWait.SpinUntil(() => ReaderCount(blocked) == 0 && ReaderCount(other) == 0,
                        TimeSpan.FromSeconds(5)).Should().BeTrue("both cleanup owners must finish even when lease release throws");
                    timedOut.IsSet.Should().BeFalse("the bounded safety net must fail the test if it expires");
                    drainError.Should().BeNull();
                }
                finally
                {
                    SharedAbandonedReader_Tests.Collect();
                    PageBuffer.FinalizedInUse = null;
                }
            }

            blocked.Dispose();
            other.Dispose();
            using (var writer = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared }))
            {
                writer.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1 });
                writer.Checkpoint();
            }
            using var reopened = new LiteDatabase(file.Filename);
            var docs = reopened.GetCollection("docs").FindAll().OrderBy(doc => doc["_id"].AsInt32).ToArray();
            docs.Select(doc => doc["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 160));
            docs.Should().OnlyContain(doc => doc["payload"].AsString == new string('x', 1024));
            reopened.GetCollection("docs").Find(Query.EQ("group", 1)).Select(doc => doc["_id"].AsInt32)
                .OrderBy(id => id).Should().Equal(Enumerable.Range(1, 160).Where(id => id % 2 == 1));
            (reopened.GetCollection("other").FindById(1) != null).Should().BeTrue();
            Volatile.Read(ref finalized).Should().Be(0);
        }

        private static FieldInfo Field(Type type, string name) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);

        private static int ReaderCount(SharedEngine engine)
        {
            var readers = (Dictionary<int, int>)Field(typeof(SharedEngine), "_localReaders").GetValue(engine);
            lock (readers) return readers.Values.Sum();
        }
    }
}
