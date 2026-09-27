using LiteDB.Tests.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CompactPowerLossFuzzer : IFuzzTarget
{
    public string Name => "compact-power-loss";
    public string Description => "Torn promotion headers/journals, repeated recovery cuts, and atomic compact schema/document recovery.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            var random = context.Random;
            var password = random.Next(2) == 0 ? null : "power-secret";
            var vector = random.Next(2) == 0;
            var kind = random.Next(5);
            var phase = kind == 0 ? PromotionPowerLossScenario.Phases[random.Next(PromotionPowerLossScenario.Phases.Length)] :
                kind == 4 ? "promotion-after-journal-page-write" :
                kind == 1 ? "promotion-before-journal-write" : "promotion-before-header-write";
            var prefix = kind == 0 || kind == 4 ? -1 : random.Next(8193);
            var recovery = kind == 3 ? new[]
            {
                "promotion-recovery-before-header-write", "promotion-recovery-after-header-write",
                "promotion-recovery-after-header-flush"
            }[random.Next(3)] : null;
            var readOnly = kind != 0 && random.Next(2) == 0;
            var damage = random.Next(2) == 0;
            // Repeated-repair probes must actually damage the primary header.
            // A long prefix can contain the entire meaningful header and need no repair.
            if (kind == 3) { prefix = 59; damage = true; }
            var journalPart = kind == 1 ? random.Next(2) : -1;
            context.Trace("compact-power-cut", new { password, vector, phase, prefix, recovery, readOnly, damage, journalPart });
            PromotionPowerLossScenario.Run(password, vector, phase, occurrence: kind == 4 ? 2 : 1, tornPrefix: prefix,
                recoveryPhase: recovery, readOnly: readOnly, damage: damage, tornJournalPart: journalPart,
                cachedJournal: kind == 4, tearRecovery: false);
            context.ObserveNovelty("compact-power-cut", kind, password != null, vector, phase, prefix / 512, readOnly);
        }
        return Task.CompletedTask;
    }
}
