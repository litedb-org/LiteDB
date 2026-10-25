using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccCheckpoint_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Backfill_PreservesUntouchedPagesAndOffsets_UntilReaderCloses(string password, bool dataSnapshot)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            if (dataSnapshot) test.Database.Checkpoint();
            using var reader = test.Engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            var snapshot = test.Engine.GetMonitor().GetThreadTransaction().Snapshots.Single();
            var version = snapshot.ReadVersion;
            var originalWal = test.Log.ToArray();
            var protectedPositions = test.Engine.GetWalIndex().SnapshotPositions(version);
            RunThread(() =>
            {
                for (var value = 1; value <= 10; value++) test.Update("docs", value);
                var count = test.Engine.Checkpoint();
                if (!dataSnapshot) count.Should().BeGreaterThan(0);
                test.Engine.GetWalIndex().BackfillVersion.Should().Be(version);
                var preamble = password == null ? 0 : Constants.PAGE_SIZE;
                foreach (var position in protectedPositions)
                {
                    test.Log.ToArray().Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE)
                        .Should().Equal(originalWal.Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE));
                }
                for (var value = 11; value <= 20; value++) test.Update("docs", value);
                test.Engine.Checkpoint();
            });
            var countRead = 1;
            reader.Current["value"].AsInt32.Should().Be(0);
            while (reader.Read())
            {
                reader.Current["value"].AsInt32.Should().Be(0);
                countRead++;
            }
            countRead.Should().Be(WalTestDatabase.DocumentCount);
            reader.Dispose();
            test.Engine.GetWalIndex().SnapshotCount.Should().Be(0);
            test.Engine.Checkpoint().Should().BeGreaterThan(0);
            test.Log.Length.Should().Be(password == null ? 0 : Constants.PAGE_SIZE);
            test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 20);
        }

        [Fact]
        public void EveryCollectionSnapshotPinsItsOwnVersion()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("first");
            test.Seed("second");
            test.Database.BeginTrans();
            var transaction = test.Engine.GetMonitor().GetThreadTransaction();
            var first = transaction.CreateSnapshot(LockMode.Read, "first", false);
            RunThread(() => test.Update("second", 1));
            var second = transaction.CreateSnapshot(LockMode.Read, "second", false);
            second.ReadVersion.Should().BeGreaterThan(first.ReadVersion);
            RunThread(() => test.Engine.Checkpoint());
            test.Engine.GetWalIndex().BackfillVersion.Should().Be(first.ReadVersion);
            first.Dispose();
            RunThread(() => test.Engine.Checkpoint());
            test.Engine.GetWalIndex().BackfillVersion.Should().Be(second.ReadVersion);
            test.Database.Rollback();
            test.Engine.Checkpoint();
            test.Log.Length.Should().Be(0);
        }

        [Fact]
        public void CheckpointCannotPassVersionCaptureBeforePinPublication()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            using var captured = new ManualResetEventSlim();
            using var publish = new ManualResetEventSlim();
            using var opened = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            using var checkpointStarted = new ManualResetEventSlim();
            using var checkpointDone = new ManualResetEventSlim();
            var wal = test.Engine.GetWalIndex();
            wal.SnapshotCaptured = () =>
            {
                captured.Set();
                publish.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            Exception readerError = null;
            var reader = new Thread(() =>
            {
                try
                {
                    using var cursor = test.Engine.Query("docs", new Query());
                    opened.Set();
                    finish.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    while (cursor.Read()) cursor.Current["value"].AsInt32.Should().Be(0);
                }
                catch (Exception ex) { readerError = ex; }
            });
            Exception checkpointError = null;
            var checkpoint = new Thread(() =>
            {
                try
                {
                    // Signal at the lock boundary, not merely before the call.
                    test.Engine.CheckpointStage = stage =>
                    {
                        if (stage == "before-index-lock") checkpointStarted.Set();
                    };
                    test.Engine.Checkpoint();
                }
                catch (Exception ex) { checkpointError = ex; }
                finally
                {
                    test.Engine.CheckpointStage = null;
                    checkpointDone.Set();
                }
            });
            reader.Start();
            try
            {
                captured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                checkpoint.Start();
                checkpointStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                checkpointDone.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
                publish.Set();
                opened.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                checkpointDone.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                test.Log.Length.Should().BeGreaterThan(0);
            }
            finally
            {
                publish.Set();
                finish.Set();
                reader.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                checkpoint.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                wal.SnapshotCaptured = null;
            }
            if (readerError != null) ExceptionDispatchInfo.Capture(readerError).Throw();
            if (checkpointError != null) ExceptionDispatchInfo.Capture(checkpointError).Throw();
        }

        internal static void RunThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue("readers must not block a writer or checkpointer");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
