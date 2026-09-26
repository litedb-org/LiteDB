#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccSharedProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-mvcc-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public MvccSharedProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task ProcessesKeepDistinctSnapshots_AndBackfillStopsAtOldestLiveReader(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using var first = new MvccProcess("hold", Filename, password);
            await first.Expect("ready");
            await MvccProcess.Run("write", Filename, password, "1");
            using var second = new MvccProcess("hold", Filename, password);
            await second.Expect("ready");
            await MvccProcess.Run("write", Filename, password, "2");
            var log = FileHelper.GetLogFile(Filename);
            var bytes = File.ReadAllBytes(log);
            await MvccProcess.Run("checkpoint", Filename, password);
            var preamble = password == null ? 0 : Constants.PAGE_SIZE;
            ((new FileInfo(log).Length - preamble) / WalChecksum.FrameSize).Should().Be(
                (bytes.Length - preamble) / WalChecksum.FrameSize + 1,
                "one retirement record appends while live offsets remain stable");
            AssertDataFileValue(password, 0);
            using (var latest = new MvccProcess("read", Filename, password))
            {
                await latest.Expect("value:2");
                await latest.Finish();
            }
            await first.Finish(true);
            await first.Expect("value:0");
            await MvccProcess.Run("checkpoint", Filename, password);
            AssertDataFileValue(password, 1);
            await MvccProcess.Run("write", Filename, password, "3");
            await second.Finish(true);
            await second.Expect("value:1");
            await MvccProcess.Run("checkpoint", Filename, password);
            File.Exists(log).Should().BeFalse();
            AssertDataFileValue(password, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task KilledReaderReleasesLease_AndKilledWriterDoesNotPublishUncommittedFrames(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using (var reader = new MvccProcess("hold", Filename, password))
            {
                await reader.Expect("ready");
                await MvccProcess.Run("write", Filename, password, "1");
                await reader.Kill();
            }
            using (var writer = new MvccProcess("uncommitted", Filename, password))
            {
                await writer.Expect("ready");
                await writer.Kill();
            }
            await MvccProcess.Run("checkpoint", Filename, password);
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();
            Directory.Exists(Filename + "-readers").Should().BeFalse("the last dead lease takes the registry with it");
            AssertDataFileValue(password, 1);
        }

        [Fact]
        public async Task ConcurrentWriterProcessesSerializeWALPublication()
        {
            await MvccProcess.Run("seed", Filename, null);
            using var reader = new MvccProcess("hold", Filename, null);
            await reader.Expect("ready");
            await Task.WhenAll(Enumerable.Range(1, 4).Select(i => MvccProcess.Run("insert", Filename, null, (100 * i).ToString())));
            await reader.Finish(true);
            await reader.Expect("value:0");
            await MvccProcess.Run("checkpoint", Filename, null);
            using var database = new LiteDatabase(Filename);
            database.GetCollection("docs").Count().Should().Be(144);
        }

        [Theory]
        [MemberData(nameof(MvccRecovery_Tests.CrashPoints), MemberType = typeof(MvccRecovery_Tests))]
        public async Task KillingCheckpointerAtEveryPublicationBoundaryRecovers(string password, string stage)
        {
            await MvccProcess.Run("seed", Filename, password);
            await MvccProcess.Run("write", Filename, password, "1");
            using (var checkpointer = new MvccProcess("crash-checkpoint", Filename, password, stage))
            {
                await checkpointer.Expect("ready");
                await checkpointer.Kill();
            }
            using (var reader = new MvccProcess("read", Filename, password))
            {
                await reader.Expect("value:1");
                await reader.Finish();
            }
            await MvccProcess.Run("checkpoint", Filename, password);
            AssertDataFileValue(password, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task KillingPartialCheckpointerDoesNotInvalidateAnotherProcessesReader(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using var reader = new MvccProcess("hold", Filename, password);
            await reader.Expect("ready");
            await MvccProcess.Run("write", Filename, password, "1");
            using (var checkpointer = new MvccProcess("crash-checkpoint", Filename, password, "data-page"))
            {
                await checkpointer.Expect("ready");
                await checkpointer.Kill();
            }
            await MvccProcess.Run("checkpoint", Filename, password);
            await MvccProcess.Run("write", Filename, password, "2");
            await reader.Finish(true);
            await reader.Expect("value:0");
            await MvccProcess.Run("checkpoint", Filename, password);
            AssertDataFileValue(password, 2);
        }

        private void AssertDataFileValue(string password, int value)
        {
            // Read a copy without the WAL to inspect the physical checkpoint boundary.
            var copy = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
            File.Copy(Filename, copy);
            // A live v13 data file is intentionally not a standalone backup: its
            // root binds the retained WAL. Clear that root only in this disposable
            // inspection copy so this test can inspect the backfilled watermark.
            using (var factory = new FileStreamFactory(copy, password, false, false))
            using (var stream = factory.GetStream(true, false))
            {
                var bytes = new byte[Constants.PAGE_SIZE];
                stream.ReadRequired(bytes, 0, bytes.Length);
                var header = new BufferSlice(bytes, 0, bytes.Length);
                new WalRetirement().WriteHeader(header);
                PageChecksum.Write(header);
                stream.Position = 0;
                stream.Write(bytes, 0, bytes.Length);
            }
            using var engine = new LiteEngine(new EngineSettings { Filename = copy, Password = password, ReadOnly = true });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            var docs = database.GetCollection("docs").FindAll().ToArray();
            docs.Should().HaveCount(64);
            docs.Should().OnlyContain(doc => doc["value"].AsInt32 == value);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
#endif
