using LiteDB.Internals;

namespace LiteDB.Fuzz.Targets;

internal sealed class MvccRetirementFuzzer : IFuzzTarget
{
    public string Name => "mvcc-retirement";
    public string Description => "Witness publication, torn slot reuse and repeated recovery with full payload/index and snapshot oracles.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            var random = context.Random;
            var password = random.Next(2) == 0 ? null : "retirement-fuzz";
            var compact = random.Next(2) == 0;
            var kind = random.Next(6);
            var priorReclaims = random.Next(4);
            var historyRounds = random.Next(1, 5);
            var phase = kind == 5 ? null : kind == 0 ? MvccRetirementScenario.Phases[random.Next(MvccRetirementScenario.Phases.Length)] :
                kind == 1 ? "retirement-before-record-write" : kind == 2 || kind == 4 ? "retirement-before-header-write" : "reuse";
            if (phase != null && phase.StartsWith("promotion-", StringComparison.Ordinal)) priorReclaims = 0;
            var prefix = kind == 0 ? -1 : kind == 4 ? 59 : random.Next(8193);
            context.Trace("mvcc-retirement", new { encrypted = password != null, compact, kind, phase, prefix, priorReclaims, historyRounds });
            MvccRetirementScenario.Run(password, compact, phase, prefix, repeatRepair: kind == 4,
                inspect: (data, log) => ChecksumFixture.Save(context, data, log),
                priorReclaims: priorReclaims, historyRounds: historyRounds, interleaveSafepoint: kind == 5);
            context.ObserveNovelty("mvcc-retirement", password != null, compact, kind, phase, prefix / 512);
        }
        return Task.CompletedTask;
    }
}
