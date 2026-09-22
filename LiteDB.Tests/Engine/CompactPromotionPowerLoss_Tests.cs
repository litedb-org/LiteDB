using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition("PromotionPowerLoss", DisableParallelization = true)]
    public class PromotionPowerLossCollection { }

    [Collection("PromotionPowerLoss")]
    public class CompactPromotionPowerLoss_Tests
    {
        [Fact]
        public void Journal_round_trips_the_complete_header()
        {
            var buffer = new LiteDB.Engine.PageBuffer(new byte[8192], 0, 0);
            var header = new LiteDB.Engine.HeaderPage(buffer, 0);
            header.EnsureVersion(10);
            header.UpdateBuffer();
            var pages = LiteDB.Engine.HeaderPromotionJournal.Encode(buffer.Array, 24576);
            Assert.True(LiteDB.Engine.HeaderPromotionJournal.IsPart(pages[0], 24576, 0));
            Assert.True(LiteDB.Engine.HeaderPromotionJournal.IsPart(pages[1], 24576, 1));
            Assert.Equal(buffer.Array, LiteDB.Engine.HeaderPromotionJournal.Decode(pages[0], pages[1], 24576));
            // A mismatched location or a torn payload must never authorize a header repair.
            Assert.Null(LiteDB.Engine.HeaderPromotionJournal.Decode(pages[0], pages[1], 16384));
            pages[1][128] ^= 1;
            Assert.Null(LiteDB.Engine.HeaderPromotionJournal.Decode(pages[0], pages[1], 24576));
        }

        public static IEnumerable<object[]> CrashCases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var vector in new[] { false, true })
            foreach (var phase in PromotionPowerLossScenario.Phases)
                yield return new object[] { password, vector, phase };
        }

        [Theory]
        [MemberData(nameof(CrashCases))]
        public void Power_loss_preserves_legacy_rows_and_atomic_compact_transaction(string password, bool vector, string phase)
        {
            PromotionPowerLossScenario.Run(password, vector, phase);
        }

        public static IEnumerable<object[]> TornCases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var prefix in new[] { 0, 1, 15, 16, 31, 48, 59, 60, 63, 64, 511, 512, 4096, 8191, 8192 })
            foreach (var damage in new[] { false, true })
                yield return new object[] { password, prefix, damage };
        }

        [Theory]
        [MemberData(nameof(TornCases))]
        public void Torn_header_is_recovered_from_durable_journal(string password, int prefix, bool damage)
        {
            PromotionPowerLossScenario.Run(password, false, "promotion-before-header-write",
                tornPrefix: prefix, damage: damage);
        }

        [Theory]
        [MemberData(nameof(TornCases))]
        public void Torn_journal_cannot_damage_the_unpromoted_database(string password, int prefix, bool damage)
        {
            for (var part = 0; part < 2; part++)
                PromotionPowerLossScenario.Run(password, false, "promotion-before-journal-write",
                    tornPrefix: prefix, damage: damage, tornJournalPart: part);
        }

        [Theory]
        [InlineData(null, "promotion-recovery-before-header-write")]
        [InlineData(null, "promotion-recovery-after-header-write")]
        [InlineData(null, "promotion-recovery-after-header-flush")]
        [InlineData("secret", "promotion-recovery-before-header-write")]
        [InlineData("secret", "promotion-recovery-after-header-write")]
        [InlineData("secret", "promotion-recovery-after-header-flush")]
        public void Repeated_power_loss_during_recovery_keeps_the_journal(string password, string phase)
        {
            PromotionPowerLossScenario.Run(password, true, "promotion-before-header-write",
                tornPrefix: 59, recoveryPhase: phase, damage: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Read_only_open_recovers_without_writing(string password)
        {
            PromotionPowerLossScenario.Run(password, true, "promotion-before-header-write",
                tornPrefix: 59, readOnly: true, damage: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Recovery_syncs_an_OS_cached_journal_before_a_torn_repair(string password)
        {
            PromotionPowerLossScenario.Run(password, false, "promotion-after-journal-page-write", occurrence: 2,
                recoveryPhase: "promotion-recovery-before-header-write", cachedJournal: true, tearRecovery: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Torn_checkpoint_after_promotion_keeps_the_recovery_image(string password)
        {
            PromotionPowerLossScenario.Run(password, true, "checkpoint-before-page-write",
                tornPrefix: 59, damage: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Two_promotions_in_one_WAL_recover_the_latest_version_and_all_commits(string password)
        {
            PromotionPowerLossScenario.Run(password, true, "promotion-before-header-write",
                tornPrefix: 59, damage: true, keepVectorJournal: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Short_journal_files_are_discarded_without_losing_committed_WAL(string password)
        {
            foreach (var prefix in new[] { 0, 1, 15, 16, 511, 4096, 8191 })
            for (var part = 0; part < 2; part++)
                PromotionPowerLossScenario.Run(password, false, "promotion-before-journal-write",
                    tornPrefix: prefix, tornJournalPart: part, shortJournal: true);
        }
    }
}
