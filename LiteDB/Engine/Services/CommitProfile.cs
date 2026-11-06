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
        [Event(3)] public void Sync(string kind) { if (IsEnabled()) WriteEvent(3, kind); }
    }
}
#endif
