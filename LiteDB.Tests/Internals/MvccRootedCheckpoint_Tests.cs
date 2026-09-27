using System.Collections.Generic;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccRootedCheckpoint_Tests
    {
        public static IEnumerable<object[]> Boundaries()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var phase in new[] { "checkpoint-before-page-write", "checkpoint-after-page-write",
                "checkpoint-before-data-flush", "checkpoint-after-data-flush", "before-reclaim",
                "checkpoint-before-clear", "checkpoint-after-clear" })
                yield return new object[] { password, compact, phase };
        }

        [Theory]
        [MemberData(nameof(Boundaries))]
        public void RootedFullCheckpoint_PowerLossPreservesPayloadsAndIndexes(string password, bool compact, string phase) =>
            MvccRootedCheckpointScenario.Run(password, compact, phase, "cut");

        public static IEnumerable<object[]> Flushes()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var phase in new[] { "before-commit-lock", "checkpoint-before-data-flush", "before-reclaim" })
                yield return new object[] { password, compact, phase };
        }

        [Theory]
        [MemberData(nameof(Flushes))]
        public void RootedFullCheckpoint_FailedSyncPreservesRecoveryEvidence(string password, bool compact, string phase) =>
            MvccRootedCheckpointScenario.Run(password, compact, phase, "flush");

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void RootedFullCheckpoint_FailedJournalSyncPreservesOriginalGeneration(string password, bool compact) =>
            MvccRootedCheckpointScenario.Run(password, compact, "before-commit-lock", "journal-flush");

        public static IEnumerable<object[]> TornWrites()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var phase in new[] { "checkpoint-before-page-write", "before-reclaim" })
            foreach (var prefix in new[] { 15, 171, 4096, 8191 })
                yield return new object[] { password, compact, phase, prefix };
        }

        [Theory]
        [MemberData(nameof(TornWrites))]
        public void RootedFullCheckpoint_TornDataOrGenerationHeaderIsRecoverable(string password, bool compact, string phase, int prefix) =>
            MvccRootedCheckpointScenario.Run(password, compact, phase, "tear", prefix);

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void TornRootRemoval_ToleratesRepeatedTornHeaderRepairs(string password, bool compact) =>
            MvccRootedCheckpointScenario.Run(password, compact, "before-reclaim", "tear", repeatRepair: true);
    }
}
