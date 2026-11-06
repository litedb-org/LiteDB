#if PROFILE
using System.Diagnostics.Tracing;

namespace LiteDB.Engine
{
    [EventSource(Name = "LiteDB-CommitProfile")]
    internal sealed class CommitProfile : EventSource
    {
        internal static readonly CommitProfile Log = new CommitProfile();
        [Event(1)] public void Group(long commits) { if (IsEnabled()) WriteEvent(1, commits); }
        [Event(2)] public void Piggyback() { if (IsEnabled()) WriteEvent(2); }
        [Event(4)] public void SyncStart(long target, long pending, long frequency) { if (IsEnabled()) WriteEvent(4, target, pending, frequency); }
        [Event(5)] public void Wait(string kind, long ticks) { if (IsEnabled()) WriteEvent(5, kind, ticks); }
        [Event(6)] public void Appended(long ticket) { if (IsEnabled()) WriteEvent(6, ticket); }
        [Event(7)] public void SyncEnd(long target) { if (IsEnabled()) WriteEvent(7, target); }
        [NonEvent] internal static long Stamp() => System.Diagnostics.Stopwatch.GetTimestamp();
        [NonEvent] internal static void Elapsed(string kind, long start) => Log.Wait(kind, Stamp() - start);
        [Event(3)] public void Sync(string kind) { if (IsEnabled()) WriteEvent(3, kind); }
    }
}
#endif
