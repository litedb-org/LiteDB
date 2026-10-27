using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(nameof(SharedAbandonedReaderCollection))]
    public class SharedReaderCleanupDispatch_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rejected_dispatch_retries_from_timer_and_releases_real_reader_pins_and_lease(bool throws)
        {
            using var file = new TempFile();
            SharedAbandonedReader_Tests.Seed(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            var reaper = (Timer)Field(typeof(SharedReaderCleanup), "Reaper").GetValue(null);
            reaper.Change(Timeout.Infinite, Timeout.Infinite);
            SharedReaderCleanup cleanup = null;
            var attempts = 0;
            var retry = 0;
            try
            {
                var weak = Abandon(engine, out cleanup);
                cleanup.DispatchOverride = (callback, state) =>
                {
                    Interlocked.Increment(ref attempts);
                    if (Volatile.Read(ref retry) != 0) return ThreadPool.UnsafeQueueUserWorkItem(callback, state);
                    if (throws) throw new OutOfMemoryException("Injected dispatch failure.");
                    return false;
                };
                SharedAbandonedReader_Tests.Collect();
                weak.IsAlive.Should().BeFalse();
                Volatile.Read(ref attempts).Should().Be(0, "finalizers must only publish owners, never dispatch or dispose them");
                Action drain = SharedReaderCleanup.Drain;
                drain.Should().NotThrow("queue failures must be contained by the reaper");
                Volatile.Read(ref attempts).Should().BeGreaterThan(0);
                RootAllocated(cleanup).Should().BeTrue();
                ReaderCount(engine).Should().Be(1, "a rejected owner must keep its lease until its cursor is disposed");
                SharedAbandonedReader_Tests.Collect();
                Volatile.Read(ref finalized).Should().Be(0, "rejection must retain the pinned resource graph");

                Volatile.Write(ref retry, 1);
                reaper.Change(20, 20);
                SpinWait.SpinUntil(() => !RootAllocated(cleanup), TimeSpan.FromSeconds(5)).Should()
                    .BeTrue("the periodic timer must retry a rejected owner without another explicit drain");
                Volatile.Read(ref attempts).Should().BeGreaterThan(1);
                Reachability.Snapshot()["fault-point:DispatchOverride"].Should().BeGreaterThan(0);
                ReaderCount(engine).Should().Be(0);
            }
            finally
            {
                Volatile.Write(ref retry, 1);
                reaper.Change(20, 20);
                try { cleanup?.Dispose(); SharedAbandonedReader_Tests.Collect(); }
                finally { PageBuffer.FinalizedInUse = null; }
            }

            engine.Dispose();
            using (var peer = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared }))
            {
                peer.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "peer" });
                peer.Checkpoint();
            }
            using var reopened = new LiteDatabase(file.Filename);
            var docs = reopened.GetCollection("docs").FindAll().OrderBy(doc => doc["_id"].AsInt32).ToArray();
            docs.Select(doc => doc["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 160));
            docs.Should().OnlyContain(doc => doc["payload"].AsString == new string('x', 1024));
            reopened.GetCollection("docs").Find(Query.EQ("group", 1)).Select(doc => doc["_id"].AsInt32)
                .OrderBy(id => id).Should().Equal(Enumerable.Range(1, 160).Where(id => id % 2 == 1));
            reopened.GetCollection("other").FindById(1)["value"].AsString.Should().Be("peer");
            Volatile.Read(ref finalized).Should().Be(0);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void False_after_queueing_and_retry_callbacks_dispose_each_owner_once(bool readerFails, bool releaseFails)
        {
            var reader = new CountingReader(readerFails);
            var releases = 0;
            var attempts = 0;
            WaitCallback delayed = null;
            object delayedState = null;
            var cleanup = new SharedReaderCleanup(reader, () =>
            {
                Interlocked.Increment(ref releases);
                if (releaseFails) throw new InvalidOperationException("Injected release failure.");
            });
            cleanup.DispatchOverride = (callback, state) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // Framework can return false after enqueueing: retain the
                    // original callback, then deliver it after a retry completes.
                    delayedState = state;
                    Volatile.Write(ref delayed, callback);
                    return false;
                }
                return ThreadPool.UnsafeQueueUserWorkItem(callback, state);
            };
            try
            {
                cleanup.Abandon();
                SharedReaderCleanup.Drain();
                SpinWait.SpinUntil(() => !RootAllocated(cleanup), TimeSpan.FromSeconds(5)).Should().BeTrue();
                Volatile.Read(ref delayed).Should().NotBeNull();
                Action lateCallback = () => delayed(delayedState);
                lateCallback.Should().NotThrow("duplicate callbacks must contain exceptions and never free the handle twice");
                cleanup.Dispose();
                reader.Disposals.Should().Be(1);
                Volatile.Read(ref releases).Should().Be(1);
                RootAllocated(cleanup).Should().BeFalse();
            }
            finally
            {
                try { cleanup.Dispose(); }
                catch (InvalidOperationException) { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(SharedEngine engine, out SharedReaderCleanup cleanup)
        {
            var reader = engine.Query("docs", new Query());
            reader.Should().BeOfType<SharedDataReader>();
            reader.Read().Should().BeTrue();
            cleanup = (SharedReaderCleanup)Field(typeof(SharedDataReader), "_cleanup").GetValue(reader);
            return new WeakReference(reader);
        }

        private static FieldInfo Field(Type type, string name) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool RootAllocated(SharedReaderCleanup cleanup) =>
            ((GCHandle)Field(typeof(SharedReaderCleanup), "_root").GetValue(cleanup)).IsAllocated;

        private static int ReaderCount(SharedEngine engine)
        {
            var readers = (Dictionary<int, int>)Field(typeof(SharedEngine), "_localReaders").GetValue(engine);
            lock (readers) return readers.Values.Sum();
        }

        private sealed class CountingReader : IBsonDataReader
        {
            private readonly bool _fails;
            internal int Disposals;
            internal CountingReader(bool fails) => _fails = fails;
            public BsonValue this[string field] => BsonValue.Null;
            public string Collection => "docs";
            public BsonValue Current => BsonValue.Null;
            public bool HasValues => false;
            public bool Read() => false;
            public void Dispose()
            {
                Interlocked.Increment(ref Disposals);
                if (_fails) throw new InvalidOperationException("Injected reader failure.");
            }
        }
    }
}
