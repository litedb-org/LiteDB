using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Delete_Tests
    {
        internal sealed class PausedSource : Stream
        {
            private readonly MemoryStream _source;
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _release;
            private bool _first = true;

            public PausedSource(byte[] bytes, ManualResetEventSlim entered, ManualResetEventSlim release)
            {
                _source = new MemoryStream(bytes);
                _entered = entered;
                _release = release;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_first)
                {
                    _first = false;
                    _entered.Set();
                    if (!_release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Upload was not released");
                }
                return _source.Read(buffer, offset, count);
            }
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => _source.Length;
            public override long Position { get => _source.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _source.Dispose();
                base.Dispose(disposing);
            }
        }

        [Theory]
        [InlineData(ConnectionType.Direct, false)]
        [InlineData(ConnectionType.Direct, true)]
        [InlineData(ConnectionType.Shared, false)]
        [InlineData(ConnectionType.Shared, true)]
        public void Delete_waits_for_the_complete_upload_without_leaving_dangling_metadata(ConnectionType mode, bool explicitDelete)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = mode });
            db.Timeout = TimeSpan.FromSeconds(3);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var deleting = new ManualResetEventSlim();
            using (var source = new MemoryStream(new byte[] { 1, 2 })) db.FileStorage.Upload("target", "old", source);
            using (var source = new MemoryStream(new byte[] { 7, 8 })) db.FileStorage.Upload("sentinel", "kept", source);
            Exception uploadFailure = null;
            Exception deleteFailure = null;
            bool deleted = false;
            var upload = new Thread(() =>
            {
                try
                {
                    using var source = new PausedSource(new byte[] { 3, 4, 5 }, entered, release);
                    db.FileStorage.Upload("target", "new", source);
                }
                catch (Exception error) { uploadFailure = error; }
            }) { IsBackground = true };
            var delete = new Thread(() =>
            {
                try
                {
                    deleting.Set();
                    if (explicitDelete) db.BeginTrans();
                    deleted = db.FileStorage.Delete("target");
                    if (explicitDelete) db.Commit();
                }
                catch (Exception error)
                {
                    deleteFailure = error;
                    try { db.Rollback(); } catch { }
                }
            }) { IsBackground = true };

            upload.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                delete.Start();
                Assert.True(deleting.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (delete.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)), "Delete did not reach its collection-lock wait");
            }
            finally
            {
                release.Set();
                Assert.True(upload.Join(TimeSpan.FromSeconds(15)));
                if ((delete.ThreadState & ThreadState.Unstarted) == 0) Assert.True(delete.Join(TimeSpan.FromSeconds(15)));
            }

            Assert.Null(uploadFailure);
            Assert.Null(deleteFailure);
            Assert.True(deleted);
            Assert.False(db.FileStorage.Exists("target"));
            Assert.Empty(db.GetCollection("_chunks").Find("_id.f = 'target'"));
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("sentinel", downloaded);
            Assert.Equal(new byte[] { 7, 8 }, downloaded.ToArray());
            db.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            Assert.False(reopened.FileStorage.Exists("target"));
            Assert.Empty(reopened.GetCollection("_chunks").Find("_id.f = 'target'"));
            Assert.True(reopened.FileStorage.Exists("sentinel"));
        }

        [Fact]
        public void Caller_can_roll_back_file_deletion_with_its_chunks()
        {
            using var db = new LiteDatabase(":memory:");
            var bytes = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE + 17).Select(i => (byte)i).ToArray();
            using (var source = new MemoryStream(bytes)) db.FileStorage.Upload("target", "file", source);
            Assert.True(db.BeginTrans());
            Assert.True(db.FileStorage.Delete("target"));
            Assert.False(db.FileStorage.Exists("target"));
            Assert.True(db.Rollback());
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("target", downloaded);
            Assert.Equal(bytes, downloaded.ToArray());
        }
    }
}
