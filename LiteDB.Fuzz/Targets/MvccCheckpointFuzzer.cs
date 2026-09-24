using LiteDB.Internals;

namespace LiteDB.Fuzz.Targets;

internal sealed class MvccCheckpointFuzzer : IFuzzTarget
{
    public string Name => "mvcc-checkpoint";
    public string Description => "Rooted v13 full checkpoint, generation rotation, torn writes and repeated repair with complete payload/index oracles.";

    public Task RunAsync(FuzzContext context)
    {
        var boundaries = new[] { "checkpoint-before-page-write", "checkpoint-after-page-write",
            "checkpoint-before-data-flush", "checkpoint-after-data-flush", "before-reclaim",
            "checkpoint-before-clear", "checkpoint-after-clear" };
        var flushes = new[] { "before-commit-lock", "checkpoint-before-data-flush", "before-reclaim" };
        while (context.Next())
        {
            var random = context.Random;
            var password = random.Next(2) == 0 ? null : "checkpoint-fuzz";
            var compact = random.Next(2) == 0;
            var kind = random.Next(5);
            var phase = kind == 0 ? boundaries[random.Next(boundaries.Length)] :
                kind == 1 ? flushes[random.Next(flushes.Length)] : kind == 2 ? "before-commit-lock" :
                kind == 3 && random.Next(2) == 0 ? "checkpoint-before-page-write" : "before-reclaim";
            var fault = kind == 0 ? "cut" : kind == 1 ? "flush" : kind == 2 ? "journal-flush" : "tear";
            var prefix = kind == 4 ? 59 : random.Next(8193);
            // Keep both recovery headers torn so the next repair must actually execute.
            var repairPrefixes = new[] { random.Next(188), random.Next(188) };
            context.Trace(Name, new { encrypted = password != null, compact, kind, phase, fault, prefix, repairPrefixes });
            MvccRootedCheckpointScenario.Run(password, compact, phase, fault, prefix, repeatRepair: kind == 4,
                inspect: (data, log) => ChecksumFixture.Save(context, data, log), repairPrefixes: repairPrefixes);
            context.ObserveNovelty(Name, password != null, compact, kind, phase, prefix / 512);
        }
        return Task.CompletedTask;
    }
}
