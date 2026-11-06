using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

// Streaming offline decoder: ignores sampled stacks and never executes database work.
internal static class GroupCounters
{
    internal static void Run(string trace, string output)
    {
        using var source = new EventPipeEventSource(trace);
        var rows = new List<object>();
        string scenario = null;
        double begin = 0;
        long frequency = 1, groups = 0, commits = 0, piggybacks = 0;
        var sizes = new Dictionary<long, long>();
        var kinds = new Dictionary<string, long>();
        var waits = new Dictionary<string, (long count, long ticks, long max)>();
        var barriers = new List<Dictionary<string, long>>();
        Dictionary<string, long> barrier = null;
        source.Dynamic.All += e =>
        {
            if (e.ProviderName == "LiteDB-ScenarioProfile")
            {
                if ((int)e.ID == 1)
                {
                    scenario = (string)e.PayloadValue(0);
                    begin = e.TimeStampRelativeMSec;
                    groups = commits = piggybacks = 0;
                    sizes.Clear(); kinds.Clear(); waits.Clear(); barriers.Clear(); barrier = null;
                }
                else if ((int)e.ID == 2 && scenario != null)
                {
                    if (commits != 5000 || barriers.Count != groups || kinds.GetValueOrDefault("group-device") != groups)
                        throw new InvalidDataException($"Incomplete counters for {scenario}: {commits} commits, {groups} groups, {barriers.Count} starts");
                    rows.Add(new { scenario, elapsedMs = e.TimeStampRelativeMSec - begin,
                        groups, commits, piggybacks, groupSizes = new Dictionary<long, long>(sizes),
                        syncKinds = new Dictionary<string, long>(kinds), barriers = barriers.ToArray(),
                        waits = waits.ToDictionary(p => p.Key, p => new { count = p.Value.count,
                            totalMs = p.Value.ticks * 1000.0 / frequency, maxMs = p.Value.max * 1000.0 / frequency }) });
                    scenario = null;
                }
                return;
            }
            if (scenario == null || e.ProviderName != "LiteDB-CommitProfile") return;
            switch ((int)e.ID)
            {
                case 1:
                    var n = Convert.ToInt64(e.PayloadValue(0));
                    groups++; commits += n; sizes[n] = sizes.GetValueOrDefault(n) + 1; break;
                case 2: piggybacks++; break;
                case 3:
                    var kind = (string)e.PayloadValue(0);
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1; break;
                case 4:
                    frequency = Convert.ToInt64(e.PayloadValue(2));
                    barrier = new Dictionary<string, long> {
                        ["target"] = Convert.ToInt64(e.PayloadValue(0)),
                        ["pendingAtStart"] = Convert.ToInt64(e.PayloadValue(1)), ["appendsDuring"] = 0 };
                    barriers.Add(barrier); break;
                case 5:
                    var name = (string)e.PayloadValue(0);
                    var ticks = Convert.ToInt64(e.PayloadValue(1));
                    var old = waits.GetValueOrDefault(name);
                    waits[name] = (old.count + 1, old.ticks + ticks, Math.Max(old.max, ticks)); break;
                case 6: if (barrier != null) barrier["appendsDuring"]++; break;
                case 7: barrier = null; break;
            }
        };
        source.Process();
        if (source.EventsLost != 0) throw new InvalidDataException($"Lost {source.EventsLost} events");
        if (rows.Count != 8) throw new InvalidDataException($"Expected eight controls, decoded {rows.Count}");
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
