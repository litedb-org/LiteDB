using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccRecovery_Tests
    {
        public static IEnumerable<object[]> CrashPoints()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var stage in new[] { "data-page", "data-flushed", "before-reclaim", "after-reclaim" })
                yield return new object[] { password, stage };
        }

        [Theory]
        [MemberData(nameof(CrashPoints))]
        public void InterruptedFullCheckpointRecoversCommittedState(string password, string stage)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Update("docs", 1);
            var hit = false;
            test.Engine.CheckpointStage = point =>
            {
                if (point != stage) return;
                hit = true;
                // Copy immediately, before any normal cleanup or retry can repair
                // the data/WAL image at the simulated process-crash boundary.
                test.Recover("docs", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
                test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
                throw new IOException("checkpoint interrupted");
            };
            try
            {
                Action checkpoint = () => test.Engine.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("checkpoint interrupted");
                hit.Should().BeTrue();
            }
            finally { test.Engine.CheckpointStage = null; }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void EveryPartialDataWriteIsRecoverable_AndUncommittedPagesRemainInvisible(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Seed("other");
            using var reader = test.Engine.Query("docs", new Query());
            var stageCount = 0;
            MvccCheckpoint_Tests.RunThread(() =>
            {
                test.Update("docs", 1);
                test.Database.BeginTrans();
                test.Update("other", 99);
                test.Engine.CheckpointStage = stage =>
                {
                    if (stage != "data-page" && stage != "data-flushed") return;
                    stageCount++;
                    test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
                    test.Recover("other", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 0);
                };
                try { test.Engine.Checkpoint().Should().BeGreaterThan(0); }
                finally
                {
                    test.Engine.CheckpointStage = null;
                    test.Database.Rollback();
                }
            });
            stageCount.Should().BeGreaterThan(2);
            while (reader.Read()) reader.Current["value"].AsInt32.Should().Be(0);
            reader.Dispose();
            test.Engine.Checkpoint();
            test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
        }

        [Fact]
        public void FailedSnapshotConstructionAndFailedCleanupReleaseVersionPins()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            test.Engine.SimulateDiskReadFail = _ => throw new InvalidOperationException("read failed");
            Action query = () => test.Engine.Query("docs", new Query());
            query.Should().Throw<InvalidOperationException>();
            test.Engine.GetWalIndex().SnapshotCount.Should().Be(0);
            test.Engine.SimulateDiskReadFail = null;
            var monitor = test.Engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Read, "docs", false);
            snapshot.CollectionPage.Buffer.Release();
            Action release = () => monitor.ReleaseTransaction(transaction);
            release.Should().Throw<AggregateException>();
            test.Engine.GetWalIndex().SnapshotCount.Should().Be(0);
            test.Engine.Checkpoint();
            test.Log.Length.Should().Be(0);
        }
    }
}
