using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LiteDB.Fuzz.Targets;

/// <summary>One explicit writer acquisition of one child in one round.</summary>
internal sealed record ContentionAcquisition(int Round, int Worker, ContentionTimingLine Timing)
{
    internal double WaitMs => (this.Timing.Acquired - this.Timing.Arrive) * 1000.0 / Stopwatch.Frequency;
    internal double HoldMs => (this.Timing.Released - this.Timing.Acquired) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Merges the children's acquisition records of each round and measures OVERTAKING: a waiter that
/// arrived later acquired writer ownership before an earlier waiter that was still waiting, that is
/// <c>arrive_i &lt; arrive_j &lt; acquired_j &lt; acquired_i</c> (j overtook i). This is the
/// "later-arriving waiter won" count of the safety-net follow-up; <c>scripts/measure-shared-contention.py</c>
/// measures API and intended-arrival latency per writer but counts no overtakings, so this target
/// defines the count. It is a metric, never a failure: Shared mode documents no fairness contract
/// (docs/shared-performance-followups.md: "This is not strict FIFO scheduling or a hard latency
/// guarantee"). It is not starvation either: every waiter here acquires, under its deadline.
/// <para>
/// Clock: <see cref="Stopwatch.GetTimestamp"/> is system-wide monotonic (Linux CLOCK_MONOTONIC,
/// Windows QueryPerformanceCounter, macOS the uptime clock), so timestamps of processes on one host
/// are comparable; the parent checks every child reports the same <see cref="Stopwatch.Frequency"/>.
/// Timestamps are taken in user code around the calls, so near-simultaneous acquisitions can be
/// ordered by scheduling noise; the count is performance evidence (class 3), not an exact order.
/// </para>
/// </summary>
internal sealed class SharedContentionTimeline
{
    internal const string FileName = "acquisitions.jsonl";
    private readonly List<double> _waits = new();
    private readonly StringBuilder _perChild = new();
    private int _acquisitions, _overtakings, _overtaken, _waitedForPeer, _rollbackHandoffs;

    /// <summary>A round's situations, for the reachability markers the parent emits.</summary>
    internal sealed record RoundSituations(int WaitedForPeer, int RollbackHandoffs, int Overtakings);

    internal RoundSituations AddRound(string directory, int round, IReadOnlyList<ContentionAcquisition> records)
    {
        var overtakenBy = new int[records.Count];
        var overtook = new int[records.Count];
        var waitedForPeer = 0;
        var rollbackHandoffs = 0;
        var overtakings = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var mine = records[i].Timing;
            var waited = false;
            var handedOff = false;
            for (var j = 0; j < records.Count; j++)
            {
                if (records[j].Worker == records[i].Worker) continue;
                var peer = records[j].Timing;
                if (mine.Arrive < peer.Arrive && peer.Acquired < mine.Acquired)
                {
                    overtakings++;
                    overtakenBy[i]++;
                    overtook[j]++;
                }
                // The peer owned the writer mutex during part of this waiter's wait, and got it first.
                if (peer.Acquired < mine.Acquired && peer.Released > mine.Arrive) waited = true;
                // A rolled-back transaction handed ownership to a peer that arrived while it was held.
                if (mine.Rollback && peer.Arrive < mine.Released && peer.Acquired > mine.Acquired) handedOff = true;
            }
            if (waited) waitedForPeer++;
            if (handedOff) rollbackHandoffs++;
        }

        using (var writer = File.AppendText(Path.Combine(directory, FileName)))
        {
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                writer.Write(System.Text.Json.JsonSerializer.Serialize(new
                {
                    round, worker = record.Worker, step = record.Timing.Step, rollback = record.Timing.Rollback,
                    waitMs = Math.Round(record.WaitMs, 3), holdMs = Math.Round(record.HoldMs, 3),
                    overtakenBy = overtakenBy[i], overtook = overtook[i]
                }));
                writer.Write('\n');
            }
        }

        foreach (var worker in records.Select(record => record.Worker).Distinct().OrderBy(worker => worker))
        {
            var indexes = Enumerable.Range(0, records.Count).Where(i => records[i].Worker == worker).ToArray();
            if (_perChild.Length > 0) _perChild.Append("; ");
            _perChild.Append(CultureInfo.InvariantCulture, $"r{round}w{worker}: acquisitions={indexes.Length} " +
                $"overtaken={indexes.Count(i => overtakenBy[i] > 0)} overtook={indexes.Sum(i => overtook[i])} " +
                $"maxWaitMs={(indexes.Length == 0 ? 0 : Math.Round(indexes.Max(i => records[i].WaitMs), 1))}");
        }
        _acquisitions += records.Count;
        _overtakings += overtakings;
        _overtaken += overtakenBy.Count(count => count > 0);
        _waitedForPeer += waitedForPeer;
        _rollbackHandoffs += rollbackHandoffs;
        _waits.AddRange(records.Select(record => record.WaitMs));
        return new RoundSituations(waitedForPeer, rollbackHandoffs, overtakings);
    }

    /// <summary>
    /// Run metrics (not hashed): <c>overtakings</c> (ordered pairs j overtook i), <c>overtakenAcquisitions</c>,
    /// <c>overtakingRate</c> = overtaken acquisitions / acquisitions, wait (arrive to acquired) maximum and p99.
    /// </summary>
    internal void WriteMetrics(Dictionary<string, object> metrics)
    {
        var waits = _waits.OrderBy(wait => wait).ToArray();
        metrics["acquisitions"] = _acquisitions;
        metrics["overtakings"] = _overtakings;
        metrics["overtakenAcquisitions"] = _overtaken;
        metrics["overtakingRate"] = _acquisitions == 0 ? 0.0 : Math.Round((double)_overtaken / _acquisitions, 4);
        metrics["waitedForPeerAcquisitions"] = _waitedForPeer;
        metrics["rollbackHandoffs"] = _rollbackHandoffs;
        metrics["maxWaitMs"] = waits.Length == 0 ? 0.0 : Math.Round(waits[^1], 3);
        metrics["p99AcquireMs"] = waits.Length == 0 ? 0.0 : Math.Round(waits[Math.Max(0, (int)Math.Ceiling(0.99 * waits.Length) - 1)], 3);
        metrics["perChild"] = _perChild.ToString();
    }
}
