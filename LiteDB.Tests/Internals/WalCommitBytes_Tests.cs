using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalCommitBytes_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FileBatch_MixesAppendAndReuseWithAPinnedPage_AndRecoversCommitOrRollback(bool rollback)
        {
            using var source = new WalCombinedWriteScenario(false, transactionPageLimit: 1000);
            source.Data = TempFile.ReadAllBytesShared(source.File.Filename);
            var database = source.Database;
            var docs = database.GetCollection("docs");
            database.BeginTrans();
            docs.Update(WalCombinedWriteScenario.Documents(2));
            var transaction = source.Engine.GetMonitor().GetThreadTransaction();
            FlushBatch();
            var positions = transaction.Pages.DirtyPages.ToDictionary(x => x.Key, x => x.Value.Position);
            positions.Count.Should().BeGreaterThan(2);
            using var reader = source.State.Disk.GetReader();
            var heldID = positions.Keys.First();
            var held = reader.ReadPage(positions[heldID], false, FileOrigin.Log);
            var original = held.Array.Skip(held.Offset).Take(PAGE_SIZE).ToArray();
            try
            {
                docs.Update(WalCombinedWriteScenario.Documents(3));
                FlushBatch();
                transaction.Pages.DirtyPages[heldID].Position.Should().BeGreaterThan(positions[heldID],
                    "a leased frame must append while other unconfirmed slots may be reused");
                positions.Should().Contain(x => x.Key != heldID && transaction.Pages.DirtyPages[x.Key].Position == x.Value,
                    "the batch must include a real overwrite as well as the appended leased page");
                held.Array.Skip(held.Offset).Take(PAGE_SIZE).Should().Equal(original);
                if (rollback) database.Rollback();
                else database.Commit();
                WalCombinedWriteScenario.AssertDocuments(database, rollback ? 1 : 3);
                (source.Writer.RawStream.Length % PAGE_SIZE).Should().Be(0);
            }
            finally { held.Release(); }
            source.AssertRecovered(TempFile.ReadAllBytesShared(source.LogName), rollback ? 1 : 3);

            // A checkpoint invalidates every prior batch extent. The next writer
            // must read the new length rather than retaining the old generation.
            database.Checkpoint();
            docs.Update(WalCombinedWriteScenario.Documents(4));
            source.Data = TempFile.ReadAllBytesShared(source.File.Filename);
            source.AssertRecovered(TempFile.ReadAllBytesShared(source.LogName), 4);

            void FlushBatch()
            {
                // Trigger one safepoint after the entire update so the fixture
                // distinguishes per-batch accounting from one-page batches.
                transaction.MaxTransactionSize = 0;
                transaction.Safepoint();
                transaction.MaxTransactionSize = 1000;
            }
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public void Writer_MatchesPhase1BytesAcrossTransactionsRolloverReuseAndTruncation(bool encrypted, bool checksummed)
        {
            using var actualFile = new TempFile();
            using var expectedFile = new TempFile();
            // Copy one encrypted preamble so the independent writers share the AES salt.
            using (var seed = new FileStream(actualFile.Filename, FileMode.Create, FileAccess.ReadWrite))
            {
                if (encrypted)
                {
                    using var aes = new AesStream("identity", seed);
                    aes.Flush();
                }
            }
            File.Copy(actualFile.Filename, expectedFile.Filename);
            using var actualPhysical = Open(actualFile);
            using var expectedPhysical = Open(expectedFile);
            using Stream actualRaw = encrypted ? new AesStream("identity", actualPhysical) : actualPhysical;
            using Stream expectedRaw = encrypted ? new AesStream("identity", expectedPhysical) : expectedPhysical;
            var actualChecksum = Checksum(checksummed);
            var expectedChecksum = Checksum(checksummed);
            using var actual = new ChecksummedWalStream(actualRaw, actualChecksum);
            using var expected = new BaselineWalStream(expectedRaw, expectedChecksum);
            var frames = 0;

            // Exercise all 128 trailer/padding alignments, including zero padding,
            // with both single-frame and multi-frame transactions on a reused buffer.
            for (var transaction = 1; transaction <= 132; transaction++)
            {
                var count = transaction <= 129 ? 1 : 5;
                for (var i = 0; i < count; i++)
                {
                    var page = Page(transaction, i == count - 1);
                    var original = (byte[])page.Clone();
                    if (i == count - 1)
                    {
                        actual.WriteFinalPadded(page, 37);
                        expected.WriteFinalPadded(page, 37);
                    }
                    else
                    {
                        actual.Write(page, 37, PAGE_SIZE);
                        expected.Write(page, 37, PAGE_SIZE);
                    }
                    frames++;
                    page.Should().Equal(original, "the writer must not mutate the borrowed source");
                }
                actual.Flush();
                expected.Flush();
                AssertSame();
                actual.Length.Should().Be(frames * (long)PAGE_SIZE, "padding is never a frame");
                actualChecksum.Sequence.Should().Be(checksummed ? transaction : 0);
            }

            // Reading with the enlarged buffer must still read exactly one frame.
            actual.Position = expected.Position = PAGE_SIZE;
            var actualPage = new byte[PAGE_SIZE];
            var expectedPage = new byte[PAGE_SIZE];
            actual.Read(actualPage, 0, PAGE_SIZE).Should().Be(PAGE_SIZE);
            expected.Read(expectedPage, 0, PAGE_SIZE).Should().Be(PAGE_SIZE);
            actualPage.Should().Equal(expectedPage);
            actual.Position.Should().Be(2L * PAGE_SIZE);

            // Retained bytes from the previous generation cannot leak into padding.
            actual.SetLength(0);
            expected.SetLength(0);
            actualChecksum.Reset(checksummed ? new byte[16] : null);
            expectedChecksum.Reset(checksummed ? new byte[16] : null);
            var unconfirmed = Page(200, false);
            actual.Write(unconfirmed, 37, PAGE_SIZE);
            expected.Write(unconfirmed, 37, PAGE_SIZE);
            actual.Position = expected.Position = 0;
            unconfirmed[37 + 500] ^= 0xff;
            actual.Write(unconfirmed, 37, PAGE_SIZE);
            expected.Write(unconfirmed, 37, PAGE_SIZE);
            var confirmed = Page(200, true);
            actual.WriteFinalPadded(confirmed, 37);
            expected.WriteFinalPadded(confirmed, 37);
            actual.Flush();
            expected.Flush();
            AssertSame();
            actual.Length.Should().Be(2L * PAGE_SIZE);

            void AssertSame()
            {
                actual.Position.Should().Be(expected.Position);
                actual.Length.Should().Be(expected.Length);
                actualChecksum.LastConfirmedPosition.Should().Be(expectedChecksum.LastConfirmedPosition);
                actualChecksum.Sequence.Should().Be(expectedChecksum.Sequence);
                TempFile.ReadAllBytesShared(actualFile).Should().Equal(TempFile.ReadAllBytesShared(expectedFile));
            }
        }

        private static FileStream Open(TempFile file) =>
            new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        private static WalChecksum Checksum(bool enabled)
        {
            var checksum = new WalChecksum();
            if (enabled) checksum.Reset(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
            return checksum;
        }

        private static byte[] Page(int transaction, bool confirmed)
        {
            var bytes = new byte[PAGE_SIZE + 74];
            new Random(transaction).NextBytes(bytes);
            var page = new BufferSlice(bytes, 37, PAGE_SIZE);
            page.Write((uint)transaction, BasePage.P_PAGE_ID);
            page.Write((uint)transaction, BasePage.P_TRANSACTION_ID);
            page.Write(confirmed, BasePage.P_IS_CONFIRMED);
            return bytes;
        }
    }
}
