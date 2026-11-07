using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalCombinedFallback_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AlignedPadding_SkipsSetLengthButStillInitializesRequestedZeros(bool initialize)
        {
            using var stream = new RefuseSetLengthStream();
            var original = new byte[2 * PAGE_SIZE];
            for (var i = 0; i < original.Length; i++) original[i] = 0x5a;
            stream.Write(original, 0, original.Length);
            WalPadding.Pad(stream, WalChecksum.FrameSize, initialize);
            var expected = (byte[])original.Clone();
            if (initialize) Array.Clear(expected, WalChecksum.FrameSize, expected.Length - WalChecksum.FrameSize);
            stream.ToArray().Should().Equal(expected);
        }

        [Theory]
        [InlineData(false, "padding-secret")]
        [InlineData(true, null)]
        [InlineData(true, "padding-secret")]
        public void EngineOwnedEncryptedAndCallerOwnedFileStreams_UseSeparateFrameAndPad(bool caller, string password)
        {
            using var file = new TempFile();
            using var callerFile = new TempFile();
            using var callerLog = caller ? new FileStream(callerFile.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read) : null;
            var settings = new EngineSettings { Filename = file.Filename, Password = password, LogStream = callerLog };
            using var engine = new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var state = (EngineState)typeof(LiteEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            var hits = 0;
            state.AtCrashPoint = phase =>
            {
                if (phase != "wal-confirmation-after-write") return;
                var writer = (ChecksummedWalStream)state.Disk.WalWriterLock;
                writer.RawStream.GetType().Should().NotBe(typeof(FileStream), "the engine must wrap caller files and encryption");
                var end = writer.Position / PAGE_SIZE * WalChecksum.FrameSize;
                writer.RawStream.Length.Should().Be(end, "fallback writes the frame before compatibility padding");
                (end % PAGE_SIZE).Should().NotBe(0, "the fixture must distinguish separate frame and padding writes");
                hits++;
            };
            try
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "fallback" });
                hits.Should().Be(1);
                var name = caller ? callerFile.Filename : FileHelper.GetLogFile(file.Filename);
                (new FileInfo(name).Length % PAGE_SIZE).Should().Be(0, "padding must still occur before acknowledgement");
            }
            finally { state.AtCrashPoint = null; }
            db.Checkpoint();
            db.GetCollection("docs").FindById(1)["payload"].AsString.Should().Be("fallback");
        }

        private sealed class RefuseSetLengthStream : MemoryStream
        {
            public override void SetLength(long value) => throw new IOException("redundant SetLength must not be called");
        }
    }
}
