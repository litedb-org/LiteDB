using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2806_Tests
    {
        private sealed class InterruptedSource : MemoryStream
        {
            private readonly long _failAt;
            public InterruptedSource(byte[] bytes, long failAt) : base(bytes) { _failAt = failAt; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= _failAt) throw new IOException("injected upload interruption");
                return base.Read(buffer, offset, (int)Math.Min(count, _failAt - Position));
            }
            public override void CopyTo(Stream destination, int bufferSize)
            {
                var buffer = new byte[bufferSize];
                int count;
                while ((count = Read(buffer, 0, buffer.Length)) != 0) destination.Write(buffer, 0, count);
            }
        }

        [Fact]
        public void Failed_reupload_preserves_previous_bytes_and_metadata_after_reopen()
        {
            using var file = new TempFile();
            var original = Enumerable.Range(0, 620000).Select(i => (byte)(i * 31)).ToArray();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.FileStorage.Upload("asset", "old.bin", new MemoryStream(original));
                using var source = new InterruptedSource(new byte[700000], 310000);
                Action upload = () => db.FileStorage.Upload("asset", "new.bin", source);
                upload.Should().Throw<IOException>().WithMessage("injected upload interruption");
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                using var download = new MemoryStream();
                db.FileStorage.Download("asset", download);
                download.ToArray().Should().Equal(original);
                db.FileStorage.FindById("asset").Length.Should().Be(original.Length);
                db.FileStorage.FindById("asset").Filename.Should().Be("old.bin");
            }
        }

        [Fact]
        public void Missing_chunks_cause_an_explicit_read_error_and_reupload_repairs_them()
        {
            using var db = new LiteDatabase(":memory:");
            db.FileStorage.Upload("asset", "old.bin", new MemoryStream(new byte[5000]));
            db.GetCollection("_chunks").DeleteAll().Should().BeGreaterThan(0);
            Action read = () => db.FileStorage.Download("asset", new MemoryStream());
            read.Should().Throw<LiteException>("missing chunks must not be reported as a successful empty download");
            var replacement = new byte[] { 17, 21, 0, 255 };
            db.FileStorage.Upload("asset", "new.bin", new MemoryStream(replacement));
            using var download = new MemoryStream();
            db.FileStorage.Download("asset", download);
            download.ToArray().Should().Equal(replacement);
            db.FileStorage.FindById("asset").Length.Should().Be(replacement.Length);
            db.GetCollection("_chunks").Count().Should().Be(1);
        }
    }
}
