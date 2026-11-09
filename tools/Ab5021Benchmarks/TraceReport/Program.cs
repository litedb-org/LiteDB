using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
if (log.EventsLost != 0) throw new Exception("Lost events: " + log.EventsLost);
var rows = new List<object>();
string? scenario = null;
var spans = new Dictionary<string, List<double>>();
var frames = new Dictionary<string, int>();
double begin = 0;
foreach (var e in log.Events)
{
    if (e.ProviderName == "LiteDB-ScenarioProfile")
    {
        if ((int)e.ID == 1) { scenario = (string)e.PayloadValue(0); begin = e.TimeStampRelativeMSec; spans.Clear(); frames.Clear(); }
        if ((int)e.ID == 2 && scenario != null)
        {
            rows.Add(new { scenario, elapsedMs = e.TimeStampRelativeMSec - begin,
                spans = spans.ToDictionary(p => p.Key, p => new { count = p.Value.Count, ms = p.Value.Sum() }),
                frames = frames.OrderByDescending(p => p.Value).ToArray() });
            scenario = null;
        }
    }
    if (scenario == null) continue;
    if (e.ProviderName == "LiteDB-SharedOpenProfile" && (int)e.ID == 1)
    {
        var name = (string)e.PayloadValue(0);
        if (!spans.ContainsKey(name)) spans[name] = new List<double>();
        spans[name].Add(Convert.ToDouble(e.PayloadValue(1)));
    }
    if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
    {
        var stack = e.CallStack();
        while (stack != null)
        {
            var name = stack.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name)) frames[name] = frames.GetValueOrDefault(name) + 1;
            stack = stack.Caller;
        }
    }
}
File.WriteAllText(args[1], JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
