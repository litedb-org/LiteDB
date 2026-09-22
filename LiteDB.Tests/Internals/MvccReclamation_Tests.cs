using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccReclamation_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ObsoleteFramesBecomeReusable_WhileFloorFramesRemainUnchanged(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("cold");
            test.Seed("docs");
            for (var value = 1; value <= 20; value++) test.Update("docs", value);
            using var reader = test.Engine.Query("docs", new Query());
            var version = test.Engine.ReadVersion;
            var before = test.Log.ToArray();
            var pinned = test.Engine.GetWalIndex().SnapshotPositions(version);
            var preamble = password == null ? 0 : Constants.PAGE_SIZE;
            MvccCheckpoint_Tests.RunThread(() =>
            {
                test.Engine.Checkpoint();
                var reclaimedLength = test.Log.Length;
                for (var value = 21; value <= 25; value++) test.Update("cold", value);
                // Payload frames reuse witnessed holes; five confirmations append.
                // Witness pages are counted separately from update growth.
                ((test.Log.Length - preamble) / WalChecksum.FrameSize).Should().Be(
                    (reclaimedLength - preamble) / WalChecksum.FrameSize + 5);
                var after = test.Log.ToArray();
                foreach (var position in pinned)
                {
                    after.Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE)
                        .Should().Equal(before.Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE));
                }
                test.Recover("cold", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 25);
                test.Recover("cold", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 25);
            });
            var count = 0;
            while (reader.Read())
            {
                reader.Current["value"].AsInt32.Should().Be(20);
                count++;
            }
            count.Should().Be(WalTestDatabase.DocumentCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void PartiallyObsoleteTransactionKeepsConfirmationForItsRequiredPages(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("first");
            test.Seed("second");
            test.Database.BeginTrans();
            test.Update("first", 1);
            test.Update("second", 1);
            test.Database.Commit();
            for (var value = 2; value <= 10; value++) test.Update("second", value);
            using var reader = test.Engine.Query("first", new Query());
            MvccCheckpoint_Tests.RunThread(() =>
            {
                test.Engine.Checkpoint();
                test.Update("second", 11);
                test.Recover("first", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
                test.Recover("second", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 11);
            });
            while (reader.Read()) reader.Current["value"].AsInt32.Should().Be(1);
        }

        [Theory]
        [InlineData(null, "wal-slot-cleared")]
        [InlineData("secret", "wal-slot-cleared")]
        [InlineData(null, "wal-slots-flushed")]
        [InlineData("secret", "wal-slots-flushed")]
        [InlineData(null, "wal-slots-published")]
        [InlineData("secret", "wal-slots-published")]
        public void RecoveryWorksAtEveryReclamationBoundary(string password, string crashPoint)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("cold");
            test.Seed("docs");
            for (var value = 1; value <= 5; value++) test.Update("docs", value);
            using var reader = test.Engine.Query("docs", new Query());
            var hit = 0;
            test.Engine.CheckpointStage = stage =>
            {
                if (stage != crashPoint) return;
                hit++;
                test.Recover("docs", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 5);
                test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 5);
            };
            try { test.Engine.Checkpoint(); }
            finally { test.Engine.CheckpointStage = null; }
            hit.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedWriteIntoReclaimedSlotRemainsUncommitted(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("cold");
            test.Seed("docs");
            for (var value = 1; value <= 5; value++) test.Update("docs", value);
            using var reader = test.Engine.Query("docs", new Query());
            test.Engine.Checkpoint();
            var length = test.Log.Length;
            var reused = false;
            test.Engine.SimulateDiskWriteFail = page =>
            {
                if (page.Position >= length - Constants.PAGE_SIZE) return;
                reused = true;
                throw new IOException("reused slot failed");
            };
            MvccCheckpoint_Tests.RunThread(() =>
            {
                Action write = () => test.Update("cold", 6);
                write.Should().Throw<IOException>();
            });
            reused.Should().BeTrue();
            test.Recover("cold", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 0);
            test.Recover("cold", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 0);
        }
    }
}
