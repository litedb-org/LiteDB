using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;

// Offline trace decoding only. Samples are managed sampled-thread-time, not hardware CPU cycles.
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
var rows = new List<object>();
string scenario = null;
double begin = 0;
long samples = 0, missing = 0, syncs = 0, groups = 0, commits = 0, piggybacks = 0;
var inclusive = new Dictionary<string, long>();
var exclusive = new Dictionary<string, long>();
var sizes = new Dictionary<long, long>();
long frequency = 1;
var waits = new Dictionary<string, (long count, long ticks, long max)>();
var barriers = new List<Dictionary<string, long>>();
Dictionary<string, long> currentBarrier = null;
var syncKinds = new Dictionary<string, long>();
void Add(Dictionary<string, long> table, string key) => table[key] = table.GetValueOrDefault(key) + 1;
foreach (var e in log.Events)
{
    if (e.ProviderName == "LiteDB-ScenarioProfile")
    {
        if ((int)e.ID == 1)
        {
            scenario = (string)e.PayloadValue(0);
            begin = e.TimeStampRelativeMSec;
            samples = missing = syncs = groups = commits = piggybacks = 0;
            inclusive.Clear(); exclusive.Clear(); sizes.Clear(); syncKinds.Clear(); waits.Clear(); barriers.Clear(); currentBarrier = null;
        }
        else if ((int)e.ID == 2 && scenario != null)
        {
            rows.Add(new { scenario, elapsedMs = e.TimeStampRelativeMSec - begin, samples, missing,
                syncs, groups, commits, piggybacks, groupSizes = new Dictionary<long, long>(sizes),
                syncKinds = new Dictionary<string, long>(syncKinds),
                barriers,
                waits = waits.ToDictionary(p => p.Key, p => new { count = p.Value.count,
                    totalMs = p.Value.ticks * 1000.0 / frequency, maxMs = p.Value.max * 1000.0 / frequency }),
                inclusive = inclusive.OrderByDescending(p => p.Value).Take(80).ToArray(),
                exclusive = exclusive.OrderByDescending(p => p.Value).Take(80).ToArray() });
            scenario = null;
        }
        continue;
    }
    if (scenario == null) continue;
    if (e.ProviderName == "LiteDB-CommitProfile")
    {
        switch ((int)e.ID)
        {
            case 1:
                var n = Convert.ToInt64(e.PayloadValue(0));
                groups++; commits += n; sizes[n] = sizes.GetValueOrDefault(n) + 1; break;
            case 4:
                frequency = Convert.ToInt64(e.PayloadValue(2));
                currentBarrier = new Dictionary<string, long> {
                    ["target"] = Convert.ToInt64(e.PayloadValue(0)),
                    ["pendingAtStart"] = Convert.ToInt64(e.PayloadValue(1)), ["appendsDuring"] = 0 };
                barriers.Add(currentBarrier); break;
            case 5:
                var kind = (string)e.PayloadValue(0);
                var ticks = Convert.ToInt64(e.PayloadValue(1));
                var previous = waits.GetValueOrDefault(kind);
                waits[kind] = (previous.count + 1, previous.ticks + ticks, Math.Max(previous.max, ticks)); break;
            case 6:
                if (currentBarrier != null) currentBarrier["appendsDuring"]++; break;
            case 7: currentBarrier = null; break;
            case 2: piggybacks++; break;
            case 3: syncs++; Add(syncKinds, (string)e.PayloadValue(0)); break;
        }
    }
    if (e.ProviderName != "Microsoft-DotNETCore-SampleProfiler" || (int)e.ID != 0) continue;
    samples++;
    var stack = e.CallStack();
    if (stack == null) { missing++; continue; }
    var frames = new HashSet<string>();
    bool leaf = true;
    for (; stack != null; stack = stack.Caller)
    {
        var name = stack.CodeAddress.FullMethodName;
        if (string.IsNullOrEmpty(name)) name = stack.CodeAddress.ToString();
        if (leaf) { Add(exclusive, name); leaf = false; }
        if (frames.Add(name)) Add(inclusive, name);
    }
}
File.WriteAllText(args[1], JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
if (rows.Count == 0) throw new Exception("No scenario boundaries decoded");
