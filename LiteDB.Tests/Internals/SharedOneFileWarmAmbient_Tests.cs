#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedReaderCleanup")]
    public class SharedOneFileWarmAmbient_Tests
    {
        private static readonly AsyncLocal<object> Ambient = new AsyncLocal<object>();

        [OneFileTheory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Default_timeout_process_timer_does_not_retain_original_facade_during_peer_stream(int mode)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var peer = new SharedEngine(files.Settings);
            var table = peer.MappedReaderTable;
            var cache = table.ReadCache;
            cache.IdleLimit.Should().Be(TimeSpan.FromMilliseconds(100));
            WeakReference weak = null;
            Exception error = null;
            using var warmed = new ManualResetEventSlim();
            var creator = new Thread(() =>
            {
                try { weak = Warm(files.Settings, mode); }
                catch (Exception ex) { error = ex; }
                finally { warmed.Set(); }
            }) { IsBackground = true };
            creator.Start();
            IBsonDataReader reader = null;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (reader == null && DateTime.UtcNow < deadline && error == null)
                {
                    lock (cache.Gate)
                    {
                        if (cache.Idle != null) reader = peer.Query("docs", Query.All());
                    }
                    if (reader == null) Thread.Sleep(1);
                }
                reader.Should().NotBeNull("the peer must join the actual process cache while its production timer is installed");
                warmed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                creator.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                error.Should().BeNull();
                peer.CoordinatedReadHits.Should().BeGreaterThan(0);
                table.HasReaders.Should().BeTrue();
                SpinWait.SpinUntil(() =>
                {
                    SharedReaderCleanupCycles_Tests.Collect();
                    return !weak.IsAlive;
                }, TimeSpan.FromSeconds(5)).Should().BeTrue("a peer's active result must not retain the opening caller's AsyncLocal facade");
                table.HasReaders.Should().BeTrue("the peer result remains live during collection");
                var rows = 0;
                while (reader.Read())
                {
                    reader.Current["payload"].AsString.Should().Be(new string('x', 1024));
                    reader.Current["value"].AsInt32.Should().Be(0);
                    rows++;
                }
                rows.Should().Be(160);
                reader.Dispose();
                reader = null;
                peer.Dispose();
                SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
                files.WriteIsFree();
                using var reopened = new LiteDatabase(files.Filename);
                Verify(reopened, 0);
            }
            finally
            {
                reader?.Dispose();
                creator.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Warm(EngineSettings settings, int mode)
        {
            var original = new SharedEngine(settings);
            // Start the inherited holder before setting ambient state, isolating
            // the production snapshot timer from the separate thread-flow edge.
            using (var initial = original.Query("docs", new Query { Limit = 1 })) initial.Read().Should().BeTrue();
            if (mode != 0) Ambient.Value = original;
            try
            {
                if (mode == 2) { using (ExecutionContext.SuppressFlow()) WarmReads(original); }
                else WarmReads(original);
                original.HasCachedSnapshot.Should().BeTrue();
            }
            finally { Ambient.Value = null; }
            var owner = Field<SharedMutexOwner>(original, "_owner");
            var holder = Field<Thread>(owner, "_holder");
            if (holder != null) holder.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            return new WeakReference(original);
        }

        private static void WarmReads(SharedEngine engine)
        {
            for (var i = 0; i < 3; i++)
            {
                using var reader = engine.Query("docs", new Query { Limit = 1 });
                reader.Read().Should().BeTrue();
            }
        }
    }
}
#endif
