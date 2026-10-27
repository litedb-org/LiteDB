using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(nameof(SharedAbandonedReaderCollection))]
    public class SharedReaderCleanupCycles_Tests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void Request_transform_cycle_collects_without_losing_pages_or_leases(bool warm, bool cycle, bool disposeReader)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            try
            {
                var weak = CreateOnExitedThread(file.Filename, warm, cycle, disposeReader);
                // Exclude the inherited mutex holder's bounded idle lifetime.
                Thread.Sleep(1800);
                SpinWait.SpinUntil(() =>
                {
                    Collect();
                    return weak.All(reference => !reference.IsAlive);
                }, TimeSpan.FromSeconds(10)).Should().BeTrue("the callback target may own its escaping reader");
                using var registry = new SharedReaderRegistry(file.Filename);
                SpinWait.SpinUntil(() => registry.LiveVersions()?.Length == 0, TimeSpan.FromSeconds(5))
                    .Should().BeTrue("the collected cursor must actually release its lease");
                Volatile.Read(ref finalized).Should().Be(0);
                VerifyAfterPeerWrite(file.Filename);
                Collect();
                Volatile.Read(ref finalized).Should().Be(0);
            }
            finally { Collect(); PageBuffer.FinalizedInUse = null; }
        }

        [Fact]
        public void Live_request_keeps_its_transform_and_cursor_usable_until_explicit_disposal()
        {
            using var file = new TempFile();
            Seed(file.Filename);
            var request = new Request();
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename, ReadTransform = request.Transform });
            using var reader = engine.Query("docs", Query.All());
            request.Reader = reader;
            Collect();
            var count = 0;
            while (reader.Read())
            {
                reader.Current["payload"].AsString.Should().Be(new string('x', 1024));
                count++;
            }
            count.Should().Be(160);
            request.Calls.Should().Be(160, "the original stateful transform must run exactly once per row");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CreateOnExitedThread(string filename, bool warm, bool cycle, bool disposeReader)
        {
            WeakReference[] weak = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { weak = Abandon(filename, warm, cycle, disposeReader); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            error.Should().BeNull();
            return weak;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Abandon(string filename, bool warm, bool cycle, bool disposeReader)
        {
            var request = new Request();
            var engine = new SharedEngine(new EngineSettings { Filename = filename, ReadTransform = request.Transform });
            if (warm)
            {
                using var previous = engine.Query("docs", Query.All());
                previous.Read().Should().BeTrue();
            }
            var reader = engine.Query("docs", Query.All());
            reader.Should().BeOfType<SharedDataReader>();
            reader.Read().Should().BeTrue();
            request.Calls.Should().BeGreaterThan(0);
            if (cycle) request.Reader = reader;
            if (disposeReader) reader.Dispose();
            return new[] { new WeakReference(reader), new WeakReference(engine), new WeakReference(request) };
        }

        internal static void VerifyAfterPeerWrite(string filename)
        {
            using (var peer = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared }))
            {
                peer.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "peer" });
                peer.Checkpoint();
            }
            using var reopened = new LiteDatabase(filename);
            var docs = reopened.GetCollection("docs").FindAll().OrderBy(doc => doc["_id"].AsInt32).ToArray();
            docs.Select(doc => doc["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 160));
            docs.Should().OnlyContain(doc => doc["payload"].AsString == new string('x', 1024));
            reopened.GetCollection("docs").Find(Query.EQ("group", 1)).Select(doc => doc["_id"].AsInt32)
                .OrderBy(id => id).Should().Equal(Enumerable.Range(1, 160).Where(id => id % 2 == 1));
            reopened.GetCollection("other").FindById(1)["value"].AsString.Should().Be("peer");
        }

        internal static void Seed(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.GetCollection("docs").Insert(Enumerable.Range(1, 160).Select(id =>
                new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 1024), ["group"] = id % 2 }));
            db.GetCollection("docs").EnsureIndex("group");
        }

        internal static void Collect()
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }

        private sealed class Request
        {
            internal IBsonDataReader Reader;
            internal int Calls;
            // The instance target owns the reader; the transform need not use it.
            internal BsonValue Transform(string collection, BsonValue value) { Calls++; return value; }
        }
    }
}
