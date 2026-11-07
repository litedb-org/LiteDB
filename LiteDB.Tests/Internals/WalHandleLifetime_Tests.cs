#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class WalHandleLifetime_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Wal_handle_preserves_exclusivity_truncation_and_disposal(bool durable)
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, DurableCommits = durable };
            using var factory = settings.CreateLogFactory();
            // A factory, like DiskService, captures the setting at open, even when
            // its writer is created lazily after the caller changes the settings.
            settings.DurableCommits = !durable;
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                using (var stream = (FileStream)factory.GetStream(true, false))
                {
                    stream.IsAsync.Should().Be(durable && RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
                    handle = stream.SafeFileHandle;
                    stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
                    stream.Flush();
                    NativeFileSync.FlushDevice(handle, logName);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        Action secondWriter = () => { using var other = new FileStream(logName,
                            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); };
                        secondWriter.Should().Throw<IOException>();
                    }
                    using (var reader = (FileStream)factory.GetStream(false, false))
                    {
                        reader.IsAsync.Should().BeFalse();
                        reader.ReadByte().Should().Be(1);
                    }
                    stream.SetLength(0);
                    stream.Length.Should().Be(0);
                    stream.Position = 0;
                    stream.WriteByte(42);
                    stream.Flush();
                    NativeFileSync.FlushDevice(handle, logName);
                }
                handle.IsClosed.Should().BeTrue();
                File.ReadAllBytes(logName).Should().Equal(42);
                using (var exclusive = new FileStream(logName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    exclusive.Length.Should().Be(1);
            }
            finally { File.Delete(logName); }
        }

        [Fact]
        public void Wal_caller_file_stream_retains_buffering_until_flush()
        {
            using var file = new TempFile();
            using var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite,
                FileShare.Read, 65536, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? FileOptions.Asynchronous : FileOptions.None);
            var checksum = new WalChecksum();
            checksum.Reset(new byte[16]);
            using var writer = new ChecksummedWalStream(raw, checksum);
            var page = new byte[8192];
            var slice = new BufferSlice(page, 0, page.Length);
            slice.Write(1u, BasePage.P_PAGE_ID);
            slice.Write(1u, BasePage.P_TRANSACTION_ID);
            slice[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
            slice[BasePage.P_PAGE_FORMAT] = PageChecksum.Checksummed;
            writer.Write(page, 0, page.Length);
            new FileInfo(file.Filename).Length.Should().Be(0);
            writer.Flush();
            new FileInfo(file.Filename).Length.Should().Be(16384);
            writer.Position = 0;
            var actual = new byte[8192];
            writer.Read(actual, 0, actual.Length).Should().Be(actual.Length);
            actual.Should().Equal(page);
        }

        [Fact]
        public void Wal_completed_native_writes_preserve_offsets_bytes_and_reopen()
        {
            using var file = new TempFile();
            using var factory = new FileStreamFactory(file.Filename, null, false, false, isLog: true);
            var expected = new byte[32768];
            new Random(92).NextBytes(expected);
            using (var stream = (FileStream)factory.GetStream(true, false))
            using (var writer = new WalFileWrite(stream))
            {
                // Bind to FileStream's IOCP first; native writes must suppress packets
                // whose OVERLAPPED belongs to our stack rather than the runtime.
                stream.Write(expected, 0, expected.Length);
                for (var i = 0; i < 100; i++)
                {
                    var offset = i % 2 * 16384;
                    stream.Position = offset;
                    if (stream.IsAsync) writer.Write(expected, offset, 16384);
                    else stream.Write(expected, offset, 16384);
                    stream.Position.Should().Be(offset + 16384);
                    stream.Length.Should().Be(expected.Length);
                }
                stream.SetLength(16384);
                stream.Position = 16384;
                if (stream.IsAsync) writer.Write(expected, 16384, 16384);
                else stream.Write(expected, 16384, 16384);
                stream.Length.Should().Be(expected.Length);
                NativeFileSync.FlushDevice(stream.SafeFileHandle, file.Filename);
                using var reader = (FileStream)factory.GetStream(false, false);
                reader.IsAsync.Should().BeFalse();
                var actual = new byte[expected.Length];
                reader.ReadFully(actual, 0, actual.Length).Should().Be(actual.Length);
                actual.Should().Equal(expected);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(expected);
        }

        [Fact]
        public void Wal_completed_native_write_reports_access_failure_without_mutating_file()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, new byte[] { 42 });
            using var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 8192, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? FileOptions.Asynchronous : FileOptions.None);
            using var writer = new WalFileWrite(stream);
            Action write = () =>
            {
                if (stream.IsAsync) writer.Write(new byte[8192], 0, 8192);
                else stream.Write(new byte[8192], 0, 8192);
            };
            if (stream.IsAsync) write.Should().Throw<IOException>().Which.HResult.Should().Be(unchecked((int)0x80070005));
            else write.Should().Throw<NotSupportedException>();
            stream.Length.Should().Be(1);
            File.ReadAllBytes(file.Filename).Should().Equal(42);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Wal_reopens_after_checkpoint_rebuild_and_disposal(bool encrypted)
        {
            using var file = new TempFile();
            for (var cycle = 0; cycle < 4; cycle++)
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = encrypted ? "handle-test" : null,
                    DurableCommits = cycle % 2 == 0
                });
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value", true);
                for (var id = 0; id < cycle; id++)
                    rows.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
                rows.Insert(new BsonDocument { ["_id"] = cycle, ["value"] = cycle });
                db.Checkpoint();
                db.Rebuild();
                db.GetCollection("rows").Count().Should().Be(cycle + 1);
            }
            using var exclusive = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
#endif
