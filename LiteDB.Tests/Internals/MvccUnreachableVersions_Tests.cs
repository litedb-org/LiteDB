using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccUnreachableVersions_Tests
    {
        private const int ROUND_UPDATES = 15;

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void VersionsWrittenAfterAReaderOpened_AreReusableOnceSuperseded(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("cold");
            test.Seed("docs");
            test.Update("docs", 1);
            using var reader = test.Engine.Query("docs", new Query());
            var version = test.Engine.ReadVersion;
            var preamble = password == null ? 0 : Constants.PAGE_SIZE;
            MvccCheckpoint_Tests.RunThread(() =>
            {
                var appended = this.GrowthOfRound(test, "cold", 1);
                // History the reader cannot see, superseded before the checkpoint.
                this.GrowthOfRound(test, "docs", 2);
                var before = test.Log.ToArray();
                var pinned = test.Engine.GetWalIndex().SnapshotPositions(version);
                test.Engine.Checkpoint();
                var reused = this.GrowthOfRound(test, "cold", 1 + ROUND_UPDATES);

                // Confirmation frames append; compare payload growth separately.
                var anchors = ROUND_UPDATES * WalChecksum.FrameSize;
                (reused - anchors).Should().BeLessThan((appended - anchors) / 2);
                var after = test.Log.ToArray();
                foreach (var position in pinned)
                {
                    after.Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE)
                        .Should().Equal(before.Skip((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize) + preamble).Take(Constants.PAGE_SIZE));
                }
                test.Recover("docs", false).Should().OnlyContain(doc => doc["value"].AsInt32 == 1 + ROUND_UPDATES);
                test.Recover("cold", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 2 * ROUND_UPDATES);
            });
            ReadAll(reader).Should().OnlyContain(value => value == 1);
        }

        [Fact]
        public void EverySnapshotKeepsItsOwnFloor_BetweenOlderAndNewerReaders()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            test.Update("docs", 1);
            using var oldest = test.Engine.Query("docs", new Query());
            MvccCheckpoint_Tests.RunThread(() =>
            {
                for (var value = 2; value <= 5; value++) test.Update("docs", value);
                using var middle = test.Engine.Query("docs", new Query());
                MvccCheckpoint_Tests.RunThread(() =>
                {
                    for (var value = 6; value <= 9; value++) test.Update("docs", value);
                    test.Engine.Checkpoint();
                    test.Update("docs", 10);
                });
                ReadAll(middle).Should().OnlyContain(value => value == 5);
            });
            ReadAll(oldest).Should().OnlyContain(value => value == 1);
            test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 10);
        }

        private long GrowthOfRound(WalTestDatabase test, string collection, int firstValue)
        {
            var length = test.Log.Length;
            for (var value = firstValue; value < firstValue + ROUND_UPDATES; value++) test.Update(collection, value);
            return test.Log.Length - length;
        }

        private static int[] ReadAll(IBsonDataReader reader)
        {
            var values = new System.Collections.Generic.List<int>();
            while (reader.Read()) values.Add(reader.Current["value"].AsInt32);
            values.Count.Should().Be(WalTestDatabase.DocumentCount);
            return values.ToArray();
        }
    }
}
