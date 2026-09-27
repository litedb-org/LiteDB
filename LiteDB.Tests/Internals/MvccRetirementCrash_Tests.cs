using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccRetirementCrash_Tests
    {
        public static IEnumerable<object[]> Boundaries()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var phase in MvccRetirementScenario.Phases)
                yield return new object[] { password, compact, phase };
        }

        [Theory]
        [MemberData(nameof(Boundaries))]
        public void EveryRetirementBoundary_PreservesAcknowledgedPayloadsAndIndexes(string password, bool compact, string phase) =>
            MvccRetirementScenario.Run(password, compact, phase);

        [Theory]
        [InlineData(null, "retirement-before-record-write")]
        [InlineData("secret", "retirement-before-record-write")]
        [InlineData(null, "retirement-before-header-write")]
        [InlineData("secret", "retirement-before-header-write")]
        [InlineData(null, "retirement-before-journal-retire-flush")]
        [InlineData("secret", "retirement-before-journal-retire-flush")]
        public void FailedDurableBarrier_StopsBeforeUnsafeReuse(string password, string phase) =>
            MvccRetirementScenario.Run(password, true, phase, failFlush: true);

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void RepeatedRetirementAndReuse_PreserveAReaderAcrossMultipleWitnessPages(string password, bool compact) =>
            MvccRetirementScenario.Run(password, compact, null, priorReclaims: 4, historyRounds: 10);

        public static IEnumerable<object[]> TornWrites()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var compact in new[] { false, true })
            foreach (var phase in new[] { "retirement-before-header-write", "retirement-before-record-write", "reuse" })
            foreach (var prefix in new[] { 1, 15, 59, 171, 512, 4096, 8191 })
                yield return new object[] { password, compact, phase, prefix };
        }

        [Theory]
        [MemberData(nameof(TornWrites))]
        public void TornRetirementAndReusedPayloads_RecoverWithoutWeakeningCommitProofs(string password, bool compact, string phase, int prefix) =>
            MvccRetirementScenario.Run(password, compact, phase, prefix);

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void TornPromotion_RemainsRecoverableAfterTwoTornRepairs(string password, bool compact) =>
            MvccRetirementScenario.Run(password, compact, "retirement-before-header-write", 59, repeatRepair: true);

        [Theory]
        [InlineData(null, "retirement-before-header-write")]
        [InlineData("secret", "retirement-before-header-write")]
        [InlineData(null, "retirement-header-flushed")]
        [InlineData("secret", "retirement-header-flushed")]
        public void PublishedRecoveryFooter_SurvivesReleasedEngineLengthRounding(string password, string phase) =>
            MvccRetirementScenario.Run(password, true, phase, inspect: (data, log) =>
            {
                // Released 5.0.21 performs this rounding before version rejection.
                // It must leave both the acknowledged frames and footer intact.
                (log.Length % 8192).Should().Be(0);
                MvccRetirementScenario.Verify(data, log, password);
            });

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void RetiredWal_ReopensFromRealFiles_AndRebuildSharesItsVerifier(string password, bool compact)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-retirement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "test.db");
            var sentinel = Path.Combine(directory, "unrelated.bin");
            File.WriteAllText(sentinel, "preserve this file");
            try
            {
                MvccRetirementScenario.Run(password, compact, null, inspect: (data, log) =>
                {
                    File.WriteAllBytes(filename, data);
                    File.WriteAllBytes(FileHelper.GetLogFile(filename), log);
                    using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password }))
                    {
                        MvccRetirementScenario.VerifyDatabase(db);
                        db.Rebuild(new RebuildOptions { Password = password });
                        MvccRetirementScenario.VerifyDatabase(db);
                    }
                    using var reopened = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
                    MvccRetirementScenario.VerifyDatabase(reopened);
                    File.ReadAllText(sentinel).Should().Be("preserve this file");
                });
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
