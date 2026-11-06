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
            inclusive.Clear(); exclusive.Clear(); sizes.Clear(); syncKinds.Clear();
        }
        else if ((int)e.ID == 2 && scenario != null)
        {
            rows.Add(new { scenario, elapsedMs = e.TimeStampRelativeMSec - begin, samples, missing,
                syncs, groups, commits, piggybacks, groupSizes = new Dictionary<long, long>(sizes),
                syncKinds = new Dictionary<string, long>(syncKinds),
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
