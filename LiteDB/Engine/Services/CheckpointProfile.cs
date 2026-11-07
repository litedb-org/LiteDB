#if PROFILE
using System.Diagnostics.Tracing;

namespace LiteDB.Engine
{
    // Diagnostic only. Native syncs count attempted API calls, not device commands.
    [EventSource(Name = "LiteDB-CheckpointProfile")]
    public sealed class CheckpointProfile : EventSource
    {
        private static readonly CheckpointProfile Log = new CheckpointProfile();
        [Event(1)] public void Attempt(string kind) { if (IsEnabled()) WriteEvent(1, kind); }
        [Event(2)] public void Phase(string name) { if (IsEnabled()) WriteEvent(2, name); }
        [Event(3)] public void Outcome(int pages, string kind) { if (IsEnabled()) WriteEvent(3, pages, kind); }
        [Event(4)] public void Write(long bytes) { if (IsEnabled()) WriteEvent(4, bytes); }
        [Event(5)] public void Sync(string path) { if (IsEnabled()) WriteEvent(5, path); }
        public static void Begin(string kind) => Log.Attempt(kind);
        public static void Mark(string phase) => Log.Phase(phase);
        public static void End(int pages, string kind) => Log.Outcome(pages, kind);
        public static void DataWrite(int bytes) => Log.Write(bytes);
        public static void NativeSync(string path) => Log.Sync(path);
        public static void V5End(int pages) => End(pages, pages == 0 ? "noop" : "full");
    }
}
#endif
