using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalCombinedFailure_Tests
    {
        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(512)]
        [InlineData(4096)]
        [InlineData(8192)]
        [InlineData(8256)]
        public async Task FailedOrPartiallyPersistedCombinedWrite_StopsBeforeWriterHandoff(int persisted)
        {
            using var source = new WalCombinedWriteScenario(false);
            var writer = source.Writer;
            var original = writer.RawStream;
            var cache = source.State.Disk.Cache;
            source.Data = LiteDB.Tests.TempFile.ReadAllBytesShared(source.File.Filename);
            using var attempted = new ManualResetEventSlim();
            Task<Exception> queued = null;
            var stoppedAtHandoff = false;
            var start = 0;
            var hits = 0;
            FileStream readOnly = null;
            var streamField = typeof(ChecksummedWalStream).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
            source.State.AtCrashPoint = phase =>
            {
                if (phase == "wal-confirmation-before-write")
                {
                    start = checked((int)(writer.Position / PAGE_SIZE * WalChecksum.FrameSize));
                    queued = Task.Run(() =>
                    {
                        attempted.Set();
                        lock (writer)
                        {
                            stoppedAtHandoff = source.State.Stopped;
                            return Record.Exception(() => source.State.Validate());
                        }
                    });
                    attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    if (persisted == -1)
                    {
                        // Keep the exact FileStream branch, but make its real Write fail.
                        readOnly = new FileStream(source.LogName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        streamField.SetValue(writer, readOnly);
                    }
                }
                if (phase == "wal-confirmation-after-write")
                {
                    Interlocked.Increment(ref hits);
                    original.Length.Should().Be(WalPadding.AlignedLength(start + WalChecksum.FrameSize));
                    // Model a short/torn physical write followed by a reported I/O failure.
                    original.SetLength(start + persisted);
                    throw new IOException("combined write failed after partial persistence");
                }
            };
            try
            {
                Action update = () => source.Database.GetCollection("docs").Update(WalCombinedWriteScenario.Documents(2));
                update.Should().Throw<IOException>();
                queued.Should().NotBeNull();
                (await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(queued);
                (await queued).Should().BeAssignableTo<IOException>();
                stoppedAtHandoff.Should().BeTrue("the next owner must see terminal stop before admission");
                hits.Should().Be(persisted == -1 ? 0 : 1);
                var bytes = LiteDB.Tests.TempFile.ReadAllBytesShared(source.LogName);
                Action later = () => source.Database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 100 });
                later.Should().Throw<IOException>();
                LiteDB.Tests.TempFile.ReadAllBytesShared(source.LogName).Should().Equal(bytes);
                cache.PinnedPages.Should().Be(0);
                cache.WritablePages.Should().Be(0);
                cache.LoadingPages.Should().Be(0);
                cache.LostFrames.Should().Be(0);
                source.AssertRecovered(bytes, 1);
            }
            finally
            {
                source.State.AtCrashPoint = null;
                readOnly?.Dispose();
                original.Dispose();
            }
        }
    }
}
