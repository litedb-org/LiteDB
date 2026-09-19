#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccReclamationProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-reclaim-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public MvccReclamationProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task ReopenedWritersReuseHolesWithoutRenumberingOlderReaders(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            await MvccProcess.Run("history", Filename, password);
            using var old = new MvccProcess("hold", Filename, password);
            await old.Expect("ready");
            var log = FileHelper.GetLogFile(Filename);
            var length = new FileInfo(log).Length;
            await MvccProcess.Run("checkpoint", Filename, password);
            await MvccProcess.Run("write-cold", Filename, password, "21");
            using var newer = new MvccProcess("hold-cold", Filename, password);
            await newer.Expect("ready");
            for (var value = 22; value <= 25; value++)
            {
                await MvccProcess.Run("write-cold", Filename, password, value.ToString());
            }
            new FileInfo(log).Length.Should().Be(length + 9 * Constants.PAGE_SIZE);
            await old.Finish(true);
            await old.Expect("value:20");
            // Only the watermark advances; the other process retains its original index.
            await MvccProcess.Run("checkpoint", Filename, password);
            await MvccProcess.Run("write", Filename, password, "26");
            await newer.Finish(true);
            await newer.Expect("value:21");
            await AssertLatest(password, 26);
            await MvccProcess.Run("checkpoint", Filename, password);
            File.Exists(log).Should().BeFalse();
        }

        [Theory]
        [InlineData(null, "wal-slot-cleared")]
        [InlineData("secret", "wal-slot-cleared")]
        [InlineData(null, "wal-slots-flushed")]
        [InlineData("secret", "wal-slots-flushed")]
        [InlineData(null, "wal-slots-published")]
        [InlineData("secret", "wal-slots-published")]
        public async Task KilledReclaimerCannotInvalidateLiveReaderOrReusedSlotRecovery(string password, string stage)
        {
            await MvccProcess.Run("seed", Filename, password);
            await MvccProcess.Run("history", Filename, password);
            using var old = new MvccProcess("hold", Filename, password);
            await old.Expect("ready");
            using (var checkpointer = new MvccProcess("crash-checkpoint", Filename, password, stage))
            {
                await checkpointer.Expect("ready");
                await checkpointer.Kill();
            }
            await MvccProcess.Run("checkpoint", Filename, password);
            await MvccProcess.Run("write-cold", Filename, password, "21");
            using (var writer = new MvccProcess("uncommitted-cold", Filename, password))
            {
                await writer.Expect("ready");
                await writer.Kill();
            }
            await AssertLatest(password, 20);
            await MvccProcess.Run("read-cold", Filename, password, "21");
            await old.Finish(true);
            await old.Expect("value:20");
            await MvccProcess.Run("checkpoint", Filename, password);
            await AssertLatest(password, 20);
            await MvccProcess.Run("read-cold", Filename, password, "21");
        }

        private async Task AssertLatest(string password, int value)
        {
            using var reader = new MvccProcess("read", Filename, password);
            await reader.Expect("value:" + value);
            await reader.Finish();
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
#endif
