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
            using var data = new System.IO.MemoryStream();
            using var log = new System.IO.MemoryStream();
            using (var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
                { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy }))) { }
            var header = data.ToArray();
            var checksums = new LiteDB.Engine.WalChecksum();
            checksums.Reset(new byte[16]);
            LiteDB.Engine.HeaderJournal.Write(log, header, false, checksums, promotion: true);
            Assert.Equal(header, LiteDB.Engine.HeaderJournal.Read(log).Header);
            var bytes = log.ToArray();
            bytes[8192 + 140] ^= 1; // Bound physical location is part of the footer checksum.
            using (var changed = new System.IO.MemoryStream(bytes)) Assert.Null(LiteDB.Engine.HeaderJournal.Read(changed));
            bytes = log.ToArray();
            bytes[128] ^= 1;
            using (var changed = new System.IO.MemoryStream(bytes)) Assert.Null(LiteDB.Engine.HeaderJournal.Read(changed));
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
        public void Recovery_keeps_a_valid_header_when_the_journal_is_only_OS_cached(string password)
        {
            PromotionPowerLossScenario.Run(password, false, "promotion-after-journal-page-write", occurrence: 2,
                cachedJournal: true);
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
        public void Compact_promotion_preserves_pending_vector_and_ordinary_commits(string password)
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
