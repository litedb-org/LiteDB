using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccReclamationRace_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void AResolvedButUnreadWalOffsetCannotBeReclaimed(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("cold");
            test.Seed("docs");
            for (var value = 1; value <= 20; value++) test.Update("docs", value);
            using var resolved = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var threadID = 0;
            var paused = 0;
            long position = -1;
            Exception error = null;
            test.Engine.BeforePageRead = (offset, origin) =>
            {
                if (Environment.CurrentManagedThreadId != threadID || origin != FileOrigin.Log ||
                    Interlocked.Exchange(ref paused, 1) != 0) return;
                position = offset;
                resolved.Set();
                resume.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
            };
            var readerThread = new Thread(() =>
            {
                try
                {
                    threadID = Environment.CurrentManagedThreadId;
                    using var reader = test.Engine.Query("docs", new Query());
                    var count = 0;
                    while (reader.Read())
                    {
                        reader.Current["value"].AsInt32.Should().Be(20);
                        count++;
                    }
                    count.Should().Be(WalTestDatabase.DocumentCount);
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            readerThread.Start();
            try
            {
                resolved.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                var offset = (int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + (password == null ? 0 : Constants.PAGE_SIZE);
                var bytes = test.Log.ToArray().Skip(offset).Take(Constants.PAGE_SIZE).ToArray();
                test.Engine.Checkpoint();
                for (var value = 1; value <= 5; value++) test.Update("cold", value);
                test.Engine.Checkpoint();
                test.Log.ToArray().Skip(offset).Take(Constants.PAGE_SIZE).Should().Equal(bytes);
            }
            finally
            {
                resume.Set();
                readerThread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
                test.Engine.BeforePageRead = null;
            }
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedClearingDoesNotPublishCapacityAndReopenCanFinishReclaiming(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            for (var value = 1; value <= 5; value++) test.Update("docs", value);
            using var reader = test.Engine.Query("docs", new Query());
            test.Engine.CheckpointStage = stage =>
            {
                if (stage == "wal-slot-cleared") throw new IOException("interrupted reclamation");
            };
            Action checkpoint = () => test.Engine.Checkpoint();
            checkpoint.Should().Throw<IOException>().WithMessage("interrupted reclamation");
            test.Recover("docs", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 5);
            test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 5);
        }
    }
}
