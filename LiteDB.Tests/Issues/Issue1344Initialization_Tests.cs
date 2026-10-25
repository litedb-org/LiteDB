using System;
using System.IO;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Initialization_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Concurrent_first_uploads_initialize_storage_without_releasing_lock_order(ConnectionType mode)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = mode });
            db.Timeout = TimeSpan.FromSeconds(3);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var secondStarted = new ManualResetEventSlim();
            Exception firstFailure = null;
            Exception secondFailure = null;
            var first = new Thread(() =>
            {
                try
                {
                    using var source = new Issue1344Delete_Tests.PausedSource(new byte[] { 1, 2 }, entered, release);
                    db.FileStorage.Upload("first", "first", source);
                }
                catch (Exception error) { firstFailure = error; }
            }) { IsBackground = true };
            var second = new Thread(() =>
            {
                try
                {
                    secondStarted.Set();
                    using var source = new MemoryStream(new byte[] { 3, 4 });
                    db.FileStorage.Upload("second", "second", source);
                }
                catch (Exception error) { secondFailure = error; }
            }) { IsBackground = true };
            first.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                second.Start();
                Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (second.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                Assert.True(first.Join(TimeSpan.FromSeconds(15)));
                if ((second.ThreadState & ThreadState.Unstarted) == 0) Assert.True(second.Join(TimeSpan.FromSeconds(15)));
            }
            Assert.Null(firstFailure);
            Assert.Null(secondFailure);
            using var firstBytes = new MemoryStream();
            using var secondBytes = new MemoryStream();
            db.FileStorage.Download("first", firstBytes);
            db.FileStorage.Download("second", secondBytes);
            Assert.Equal(new byte[] { 1, 2 }, firstBytes.ToArray());
            Assert.Equal(new byte[] { 3, 4 }, secondBytes.ToArray());
            Assert.Equal(2, db.GetCollection("_files").Count());
            Assert.Equal(2, db.GetCollection("_chunks").Count());
        }
    }
}
