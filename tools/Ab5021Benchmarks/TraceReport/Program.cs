using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;

// Offline trace decoding only. Samples are managed sampled-thread-time, not hardware CPU cycles.
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
if (log.EventsLost != 0) throw new Exception($"Trace lost {log.EventsLost} events; counts are incomplete");
var rows = new List<object>();
string scenario = null;
double syncBegin = -1, syncMs = 0;
string syncKind = null;
var syncTimes = new Dictionary<string, double>();
double begin = 0, gcPauseBegin = -1, gcPauseMs = 0;
var runtimeEvents = new Dictionary<string, long>();
var allocationTicks = new Dictionary<string, long>();
var buckets = new Dictionary<string, long>();
long samples = 0, missing = 0, syncs = 0, groups = 0, commits = 0, piggybacks = 0;
var inclusive = new Dictionary<string, long>();
var exclusive = new Dictionary<string, long>();
var stacks = new Dictionary<string, long>();
var sizes = new Dictionary<long, long>();
var syncKinds = new Dictionary<string, long>();
var attempts = new Dictionary<string, long>();
var outcomes = new Dictionary<string, long>();
var checkpointSyncs = new Dictionary<string, long>();
var phases = new Dictionary<string, double>();
long pagesCopied = 0, dataWritten = 0;
string phase = null, checkpointKind = null, actualOutcome = null;
double phaseBegin = 0, checkpointBegin = 0, checkpointMs = 0;
void FinishPhase(double now)
{
    if (phase != null) phases[phase] = phases.GetValueOrDefault(phase) + now - phaseBegin;
    phaseBegin = now;
}
void Add(Dictionary<string, long> table, string key) => table[key] = table.GetValueOrDefault(key) + 1;
foreach (var e in log.Events)
{
    if (e.ProviderName == "LiteDB-ScenarioProfile")
    {
        if ((int)e.ID == 1)
        {
            scenario = (string)e.PayloadValue(0);
            begin = e.TimeStampRelativeMSec;
            syncBegin = -1; syncMs = 0; syncTimes.Clear();
            gcPauseBegin = -1; gcPauseMs = 0; runtimeEvents.Clear(); buckets.Clear(); allocationTicks.Clear();
            samples = missing = syncs = groups = commits = piggybacks = 0;
            inclusive.Clear(); exclusive.Clear(); stacks.Clear(); sizes.Clear(); syncKinds.Clear();
            attempts.Clear(); outcomes.Clear(); checkpointSyncs.Clear(); phases.Clear();
            pagesCopied = dataWritten = 0; checkpointMs = 0; phase = checkpointKind = actualOutcome = null;
        }
        else if ((int)e.ID == 2 && scenario != null)
        {
            if (syncBegin >= 0 || checkpointKind != null) throw new Exception("Unbalanced scenario spans");
            rows.Add(new { scenario, elapsedMs = e.TimeStampRelativeMSec - begin, samples, missing,
                syncs, groups, commits, piggybacks, gcPauseMs, syncMs,
                syncTimesMs = new Dictionary<string, double>(syncTimes),
                allocationTicks = new Dictionary<string, long>(allocationTicks),
                runtimeEvents = new Dictionary<string, long>(runtimeEvents),
                buckets = new Dictionary<string, long>(buckets),
                attempts = new Dictionary<string, long>(attempts), outcomes = new Dictionary<string, long>(outcomes),
                pagesCopied, dataWritten, checkpointMs,
                checkpointSyncs = new Dictionary<string, long>(checkpointSyncs), phasesMs = new Dictionary<string, double>(phases),
                groupSizes = new Dictionary<long, long>(sizes),
                syncKinds = new Dictionary<string, long>(syncKinds),
                inclusive = inclusive.OrderByDescending(p => p.Value).Take(2000).ToArray(),
                stacks = new Dictionary<string, long>(stacks),
                exclusive = exclusive.OrderByDescending(p => p.Value).Take(2000).ToArray() });
            scenario = null;
        }
        continue;
    }
    if (scenario == null) continue;
    if (e.ProviderName == "Microsoft-Windows-DotNETRuntime")
    {
        Add(runtimeEvents, e.EventName);
        if (e.EventName.StartsWith("GC/AllocationTick"))
        {
            var ti = Array.IndexOf(e.PayloadNames, "TypeName");
            var ai = Array.IndexOf(e.PayloadNames, "AllocationAmount64");
            if (ti >= 0 && ai >= 0)
            {
                var type = Convert.ToString(e.PayloadValue(ti)) ?? "unknown";
                allocationTicks[type] = allocationTicks.GetValueOrDefault(type) + Convert.ToInt64(e.PayloadValue(ai));
            }
        }
        if (e.EventName.StartsWith("GC/SuspendEEStart")) gcPauseBegin = e.TimeStampRelativeMSec;
        if (e.EventName.StartsWith("GC/RestartEEStop") && gcPauseBegin >= 0)
        {
            gcPauseMs += e.TimeStampRelativeMSec - gcPauseBegin;
            gcPauseBegin = -1;
        }
    }
    if (e.ProviderName == "LiteDB-CheckpointProfile")
    {
        switch ((int)e.ID)
        {
            case 1:
                checkpointKind = (string)e.PayloadValue(0); Add(attempts, checkpointKind);
                phase = "admission"; actualOutcome = null;
                phaseBegin = checkpointBegin = e.TimeStampRelativeMSec; break;
            case 2:
                FinishPhase(e.TimeStampRelativeMSec);
                phase = (string)e.PayloadValue(0);
                if (phase == "full" || phase == "partial") actualOutcome = phase;
                break;
            case 3:
                FinishPhase(e.TimeStampRelativeMSec);
                pagesCopied += Convert.ToInt64(e.PayloadValue(0));
                var outcome = actualOutcome ?? (string)e.PayloadValue(1);
                Add(outcomes, checkpointKind + ":" + outcome);
                checkpointMs += e.TimeStampRelativeMSec - checkpointBegin;
                checkpointKind = phase = null; break;
            case 4: dataWritten += Convert.ToInt64(e.PayloadValue(0)); break;
            case 5:
                var path = (string)e.PayloadValue(0);
                var kind = path.StartsWith("directory:") ? "directory" : path.Contains("-log") ? "wal" : "data";
                if (kind != "directory") { syncBegin = e.TimeStampRelativeMSec; syncKind = kind + ":" + phase; }
                syncs++; Add(syncKinds, kind);
                if (checkpointKind != null) Add(checkpointSyncs, kind + ":" + phase);
                break;
            case 6:
                if (syncBegin < 0) throw new Exception("Unbalanced native sync");
                var duration = e.TimeStampRelativeMSec - syncBegin;
                syncMs += duration;
                syncTimes[syncKind] = syncTimes.GetValueOrDefault(syncKind) + duration;
                syncBegin = -1;
                break;
        }
    }
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
    var ordered = new List<string>();
    bool leaf = true;
    for (; stack != null; stack = stack.Caller)
    {
        var name = stack.CodeAddress.FullMethodName;
        if (string.IsNullOrEmpty(name)) name = stack.CodeAddress.ToString();
        if (leaf) { Add(exclusive, name); leaf = false; }
        ordered.Add(name);
        if (frames.Add(name)) Add(inclusive, name);
    }
    Add(stacks, string.Join("\n", ordered));
    var joined = string.Join("|", frames);
    var bucket = checkpointKind != null ? "checkpoint" :
        joined.Contains("PollGC") || joined.Contains("GCHeap") ? "gc-or-allocation-safepoint" :
        joined.Contains("Checksum") || joined.Contains("Crc32") ? "checksum" :
        joined.Contains("Codec") || joined.Contains("Serialize") || joined.Contains("BufferWriter") || joined.Contains("WritePage") ? "serialization-codec" :
        joined.Contains("CommitHeader") || joined.Contains("HeaderPage") ? "header" :
        joined.Contains("WalCommitGroup") ? "commit-group" :
        joined.Contains("ConfirmTransaction") || joined.Contains("PublishSnapshot") ? "snapshot-publication" :
        joined.Contains("Commit") || joined.Contains("BeginTrans") || joined.Contains("TransactionService") ? "transaction" : "other";
    Add(buckets, bucket);
}
File.WriteAllText(args[1], JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
if (rows.Count == 0) throw new Exception("No scenario boundaries decoded");
