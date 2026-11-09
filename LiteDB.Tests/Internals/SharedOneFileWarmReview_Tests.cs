#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmReview_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Collected_warm_stream_releases_its_generation_and_allows_cache_expiry(bool disposeFacade)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var cache = table.ReadCache;
            var snapshot = cache.Snapshot;
            var abandoned = AbandonWarmStream(engine);
            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            try
            {
                abandoned.IsAlive.Should().BeFalse("the escaping reader itself must have been collected");
                await AwaitCleanup(() =>
                {
                    lock (cache.Gate) return snapshot.Readers == 0;
                }, "the collected warm stream must release its snapshot reader count");
                if (disposeFacade) engine.Dispose();
                snapshot.LastUse = Environment.TickCount64 - 1000;
                cache.IdleLimit = TimeSpan.Zero;
                cache.Expire();
                await AwaitCleanup(() => !table.HasReaders && (!disposeFacade || table.Closed),
                    "the collected warm stream must release its generation and final facade");
                using (new AssertionScope())
                {
                    table.HasReaders.Should().BeFalse("a finalized result must not permanently pin a process generation");
                    snapshot.Readers.Should().Be(0);
                    cache.Snapshot.Should().BeNull();
                    if (disposeFacade) table.Closed.Should().BeTrue("no facade or result remains");
                }
                if (!disposeFacade) SharedOneFilePublicationFixture.Verify(db, 0);
            }
            finally
            {
                // Test-only cleanup of the leaked state, after recording the failure.
                // No abandoned reader remains to execute its release callback.
                lock (cache.Gate)
                {
                    snapshot.Readers = 0;
                    snapshot.Lease?.Dispose();
                    snapshot.Lease = null;
                    cache.Dispose();
                }
            }
        }

        [OneFileFact]
        public async Task Collected_warm_stream_does_not_block_a_remote_writers_WAL_drain()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var writer = new MvccProcess("one-file-warm-writer", files.Filename, null);
            await writer.Expect("ready");
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var cache = engine.MappedReaderTable.ReadCache;
            var snapshot = cache.Snapshot;
            var abandoned = AbandonWarmStream(engine);
            for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            try
            {
                abandoned.IsAlive.Should().BeFalse();
                await AwaitCleanup(() => !engine.MappedReaderTable.HasReaders,
                    "the collected warm stream must release its generation before the remote commit");
                writer.Send("commit");
                await writer.Expect("committed");
                writer.Send("checkpoint");
                await writer.Expect("checkpointed");
                var retained = File.Exists(files.Log) ? new FileInfo(files.Log).Length : 0;
                retained.Should().BeGreaterThan(0, "the live facade still holds DMS even after its reader was collected");
                lock (cache.Gate)
                {
                    snapshot.Readers = 0;
                    snapshot.Engine.Dispose();
                    snapshot.Lease?.Dispose();
                    snapshot.Lease = null;
                    cache.Dispose();
                }
                SharedOneFilePublicationFixture.Verify(db, 1);
                engine.Dispose();
                writer.Send("checkpoint");
                await writer.Expect("checkpointed");
                (File.Exists(files.Log) ? new FileInfo(files.Log).Length : 0).Should().Be(0,
                    "the same remote writer must drain after the final local participant detaches");
            }
            finally
            {
                writer.Send("exit");
                await writer.Finish();
                lock (cache.Gate)
                {
                    snapshot.Readers = 0;
                    snapshot.Lease?.Dispose();
                    snapshot.Lease = null;
                    cache.Dispose();
                }
            }
            using var cold = new LiteDatabase(new LiteDB.Engine.LiteEngine(files.Settings));
            SharedOneFilePublicationFixture.Verify(cold, 1);
        }

        [OneFileFact]
        public async Task Collected_cold_stream_releases_its_generation_without_a_cache_root()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var table = engine.MappedReaderTable;
            var finalizedInUse = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalizedInUse);
            try
            {
                var abandoned = AbandonColdStream(engine);
                engine.HasCachedSnapshot.Should().BeFalse();
                for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                abandoned.IsAlive.Should().BeFalse();
                await AwaitCleanup(() => !table.HasReaders,
                    "the collected cold stream must release its generation");
                table.HasReaders.Should().BeFalse();
                engine.Dispose();
                await AwaitCleanup(() => table.Closed,
                    "deferred cold cleanup must release the final table reference");
                table.Closed.Should().BeTrue();
                finalizedInUse.Should().Be(0, "the cold control must also release its pinned page buffers");
            }
            finally { PageBuffer.FinalizedInUse = null; }
        }

        [OneFileFact]
        public void Explicitly_disposed_warm_stream_releases_its_generation_and_preserves_peer_reads()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var peer = new SharedEngine(files.Settings);
            using var peerDb = new LiteDatabase(peer, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            using (var reader = engine.Query("docs", new Query()))
            {
                reader.Read().Should().BeTrue();
                table.HasReaders.Should().BeTrue();
                engine.Dispose();
                var count = 1;
                while (reader.Read()) count++;
                count.Should().Be(160);
            }
            table.HasReaders.Should().BeFalse();
            SharedOneFilePublicationFixture.Verify(peerDb, 0);
            peer.Dispose();
            table.Closed.Should().BeTrue();
        }

        private static async Task AwaitCleanup(Func<bool> condition, string because)
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }
            condition().Should().BeTrue("deferred cleanup must finish within 10 seconds: {0}", because);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonWarmStream(SharedEngine engine)
        {
            var hits = engine.CoordinatedReadHits;
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            reader.Current["_id"].AsInt32.Should().Be(1);
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            engine.MappedReaderTable.HasReaders.Should().BeTrue();
            return new WeakReference(reader);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonColdStream(SharedEngine engine)
        {
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.CoordinatedReadHits.Should().Be(0);
            engine.MappedReaderTable.HasReaders.Should().BeTrue();
            return new WeakReference(reader);
        }
    }
}
#endif
