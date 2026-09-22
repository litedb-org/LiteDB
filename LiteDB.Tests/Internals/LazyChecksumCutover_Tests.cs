using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class LazyChecksumCutover_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FiveHundredGiBCutoverUsesBoundedHeaderIoAndWalSpace(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Database.Checkpoint();
            using var data = new HeaderOnlyData();
            var bytes = source.Data.ToArray();
            data.Write(bytes, 0, bytes.Length);
            using var log = new MeasuredLog();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            const long dataSize = 500L * 1024 * 1024 * 1024;
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(true, false))
            {
                var header = new byte[PAGE_SIZE];
                stream.ReadRequired(header, 0, header.Length);
                new BufferSlice(header, 0, PAGE_SIZE).Write((uint)(dataSize / PAGE_SIZE - 1), HeaderPage.P_LAST_PAGE_ID);
                stream.Position = 0;
                stream.Write(header, 0, header.Length);
            }
            data.ReportedLength = dataSize + (password == null ? 0 : PAGE_SIZE);
            data.Armed = true;
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize.Should().Be(0);
                db.GetCollection("$database").FindAll().Single()["checksumCoverage"].AsString.Should().Be("Mixed");
            }
            // No physical data beyond the header exists in this sparse model:
            // touching any of it throws, rather than simulating a cheap scan.
            data.BytesRead.Should().BeLessThan(20L * PAGE_SIZE);
            data.BytesWritten.Should().Be(2L * PAGE_SIZE, "checksum and index-order publication each replace only the header");
            log.MaximumLength.Should().Be(4L * PAGE_SIZE + (password == null ? 0 : PAGE_SIZE));
            log.Length.Should().Be(WalChecksum.FrameSize + (password == null ? 0 : PAGE_SIZE),
                "the committed ordering-revision header remains in WAL when checkpoint is disabled");
        }

        private sealed class HeaderOnlyData : MemoryStream
        {
            internal bool Armed;
            internal long ReportedLength;
            internal long BytesRead;
            internal long BytesWritten;
            public override long Length => ReportedLength == 0 ? base.Length : ReportedLength;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Armed && Position + count > base.Length) throw new IOException("Cutover scanned data pages");
                var read = base.Read(buffer, offset, count);
                if (Armed) BytesRead += read;
                return read;
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position + count > base.Length) throw new IOException("Cutover rewrote data pages");
                if (Armed) BytesWritten += count;
                base.Write(buffer, offset, count);
            }
        }

        private sealed class MeasuredLog : MemoryStream
        {
            internal long MaximumLength;
            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                MaximumLength = Math.Max(MaximumLength, Length);
            }
        }
    }
}
