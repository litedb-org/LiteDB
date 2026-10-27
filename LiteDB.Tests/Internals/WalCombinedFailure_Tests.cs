using System;
using System.IO;
using System.Linq;
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
        [InlineData(false)]
        [InlineData(true)]
        public void FailedSecondFrame_PreservesTheSuccessfulBatchPrefixAndRecoveredCommit(bool partialWrite)
        {
            using var source = new WalCombinedWriteScenario(false, transactionPageLimit: 1000);
            var writer = source.Writer;
            var pages = 0;
            var start = 0L;
            byte[] prefix = null;
            source.Data = LiteDB.Tests.TempFile.ReadAllBytesShared(source.File.Filename);
            source.State.AtCrashPoint = phase =>
            {
                if (phase == "wal-page-before-write" && ++pages == 2)
                {
                    start = writer.Position / PAGE_SIZE * WalChecksum.FrameSize;
                    prefix = LiteDB.Tests.TempFile.ReadAllBytesShared(source.LogName).Take(checked((int)start)).ToArray();
                    prefix.Length.Should().Be((int)start, "the first frame must have reached the file");
                    if (partialWrite)
                    {
                        // Model a reported short append at the second physical frame.
                        writer.RawStream.Position = start;
                        writer.RawStream.Write(new byte[127], 0, 127);
                        throw new IOException("second frame append failed");
                    }
                }
                if (phase == "wal-page-after-write" && pages == 2 && !partialWrite)
                    throw new IOException("second frame append failed");
            };
            try
            {
                Action update = () => source.Database.GetCollection("docs").Update(WalCombinedWriteScenario.Documents(2));
                update.Should().Throw<IOException>().WithMessage("second frame append failed");
                pages.Should().Be(2, "the fault must follow a successful frame in the same batch");
                prefix.Should().NotBeNull();
                var bytes = LiteDB.Tests.TempFile.ReadAllBytesShared(source.LogName);
                bytes.Take(prefix.Length).Should().Equal(prefix, "cleanup must use the updated batch length");
                bytes.Length.Should().Be((int)WalPadding.AlignedLength(start));
                source.State.Stopped.Should().BeTrue();
                source.AssertRecovered(bytes, 1);
                source.State.Disk.Cache.WritablePages.Should().Be(0);
                source.State.Disk.Cache.PinnedPages.Should().Be(0);
            }
            finally { source.State.AtCrashPoint = null; }
        }

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
