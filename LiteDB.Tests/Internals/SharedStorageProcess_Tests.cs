#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class SharedStorageProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shared-storage-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string Log => FileHelper.GetLogFile(Filename);

        public SharedStorageProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null, CompactStorageMode.Legacy)]
        [InlineData("secret", CompactStorageMode.Legacy)]
        [InlineData(null, CompactStorageMode.Auto)]
        [InlineData("secret", CompactStorageMode.Auto)]
        public async Task KilledWriters_PreserveAcknowledgedPayloadsIndexesAndLiveSnapshot(string password, CompactStorageMode storage)
        {
            await Run("seed", password, storage, 0);
            using var snapshot = new MvccProcess("storage-hold", Filename, password, Options(storage, 0));
            await snapshot.Expect("ready");
            Directory.GetFiles(Filename + "-readers", "*.lease").Should().HaveCount(1);
            using (var writer = new MvccProcess("storage-acknowledged", Filename, password, Options(storage, 1)))
            {
                await writer.Expect("ready");
                new FileInfo(Log).Length.Should().BeGreaterThan(WalChecksum.FrameSize);
                await writer.Kill();
            }
            await Run("verify", password, storage, 1);
            var before = new FileInfo(Log).Length;
            using (var writer = new MvccProcess("storage-uncommitted", Filename, password, Options(storage, 2)))
            {
                await writer.Expect("ready");
                new FileInfo(Log).Length.Should().BeGreaterThan(before, "safepoints must write unconfirmed WAL frames");
                await writer.Kill();
            }
            await Run("verify", password, storage, 1);
            await MvccProcess.Run("checkpoint", Filename, password);
            await snapshot.Finish(release: true);
            await snapshot.Expect("done");
            await MvccProcess.Run("checkpoint", Filename, password);
            File.Exists(Log).Should().BeFalse();
            Directory.Exists(Filename + "-readers").Should().BeFalse();
            await Run("verify", password, storage, 1);
        }

        [Theory]
        [InlineData(null, CompactStorageMode.Legacy)]
        [InlineData("secret", CompactStorageMode.Legacy)]
        [InlineData(null, CompactStorageMode.Auto)]
        [InlineData("secret", CompactStorageMode.Auto)]
        public async Task DamagedWalTail_IsReportedWithoutPartialTransactionsAndReadOnlyMutation(string password, CompactStorageMode storage)
        {
            await Run("seed", password, storage, 0);
            await Run("write", password, storage, 1);
            var prefix = File.ReadAllBytes(Log);
            await Run("write", password, storage, 2);
            var damaged = File.ReadAllBytes(Log);
            damaged[prefix.Length + 400] ^= 1;
            File.WriteAllBytes(Log, damaged);
            var data = File.ReadAllBytes(Filename);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Run("readonly", password, storage, 1);
                File.ReadAllBytes(Filename).Should().Equal(data);
                File.ReadAllBytes(Log).Should().Equal(damaged);
            }
            await Run("recovery", password, storage, 1);
            var preamble = password == null ? 0 : PAGE_SIZE;
            var confirmedEnd = preamble + (prefix.Length - preamble) / WalChecksum.FrameSize * WalChecksum.FrameSize;
            var recovered = File.ReadAllBytes(Log);
            recovered.Length.Should().Be(prefix.Length);
            // Alignment padding is not a WAL frame and may retain discarded bytes.
            recovered.Take(confirmedEnd).Should().Equal(prefix.Take(confirmedEnd));
            await Run("verify", password, storage, 1);
            await MvccProcess.Run("checkpoint", Filename, password);
            await Run("verify", password, storage, 1);
        }

        [Theory]
        [InlineData(null, CompactStorageMode.Legacy)]
        [InlineData("secret", CompactStorageMode.Legacy)]
        [InlineData(null, CompactStorageMode.Auto)]
        [InlineData("secret", CompactStorageMode.Auto)]
        public async Task DamagedDataPage_RejectsRepeatedSharedReadsAndPreservesEvidence(string password, CompactStorageMode storage)
        {
            await Run("seed", password, storage, 0);
            long position = -1;
            using (var factory = new FileStreamFactory(Filename, password, false, false))
            using (var stream = factory.GetStream(true, false))
            {
                var bytes = new byte[PAGE_SIZE];
                for (long offset = PAGE_SIZE; offset < stream.Length; offset += PAGE_SIZE)
                {
                    stream.Position = offset;
                    stream.ReadRequired(bytes, 0, bytes.Length);
                    if (bytes[BasePage.P_PAGE_TYPE] != (byte)PageType.Data) continue;
                    bytes[400] ^= 1;
                    stream.Position = offset;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.FlushToDisk();
                    position = offset;
                    break;
                }
            }
            position.Should().BeGreaterThan(0);
            var damaged = File.ReadAllBytes(Filename);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Run("reject", password, storage, checked((int)position));
                File.ReadAllBytes(Filename).Should().Equal(damaged);
                File.Exists(Log).Should().BeFalse();
            }
        }

        private Task Run(string mode, string password, CompactStorageMode storage, int revision) =>
            MvccProcess.Run("storage-" + mode, Filename, password, Options(storage, revision));

        private static string Options(CompactStorageMode storage, int revision) => storage + "," + revision;

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
#endif
