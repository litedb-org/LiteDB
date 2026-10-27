#if NET8_0_OR_GREATER
using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // One bounded current snapshot per process participant. Retired snapshots
    // belong only to their active results and close on their final release.
    internal sealed class SharedSnapshotCache : IDisposable
    {
        internal readonly object Gate = new object();
        internal readonly SharedReadPacer Pacer = new SharedReadPacer();
        internal CachedSharedSnapshot Snapshot;
        internal int Demand;
        internal Timer Idle;
        internal TimeSpan IdleLimit = TimeSpan.FromMilliseconds(100);
#if DEBUG || TESTING
        internal WeakReference<SharedEngine> Observer;
#endif
        internal void StartExpiry()
        {
            if (Idle != null) return;
            var weak = new WeakReference<SharedSnapshotCache>(this);
            Idle = new Timer(state =>
            {
                if (((WeakReference<SharedSnapshotCache>)state).TryGetTarget(out var cache)) cache.Expire();
            }, weak, IdleLimit, Timeout.InfiniteTimeSpan);
        }
        internal void Expire()
        {
            try
            {
                lock (Gate)
                {
                    var snapshot = Snapshot;
                    if (snapshot == null) return;
                    var remaining = IdleLimit.TotalMilliseconds - (Environment.TickCount64 - snapshot.LastUse);
                    if (snapshot.Readers != 0 || remaining > 0)
                    {
                        Idle?.Change(snapshot.Readers != 0 ? IdleLimit : TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                        return;
                    }
                    Snapshot = null;
                    try
                    {
#if DEBUG || TESTING
                        if (Observer != null && Observer.TryGetTarget(out var owner)) owner.CoordinationStage?.Invoke("expiring");
#endif
                    }
                    finally
                    {
                        try { snapshot.Close(); }
                        finally { Idle?.Dispose(); Idle = null; }
                    }
                }
            }
            catch (Exception) { /* Timer cleanup cannot escape to the runtime. */ }
        }
        public void Dispose()
        {
            lock (Gate)
            {
                Idle?.Dispose();
                Idle = null;
                var snapshot = Snapshot;
                Snapshot = null;
                if (snapshot == null) return;
                snapshot.Retired = true;
                if (snapshot.Readers == 0) snapshot.Close();
            }
        }
    }

    internal sealed class CachedSharedSnapshot
    {
        internal CachedSharedSnapshot(LiteEngine engine, IDisposable lease, SharedCoordinationStatus status)
        { Engine = engine; Lease = lease; Status = status; }
        internal LiteEngine Engine { get; }
        internal IDisposable Lease { get; set; }
        internal SharedCoordinationStatus Status { get; }
        internal int Readers;
        internal bool Retired;
        internal bool HadStreamingReader;
        internal long LastUse = Environment.TickCount64;
        internal void Close()
        {
            try { Engine.Dispose(); }
            finally { Lease?.Dispose(); Lease = null; }
        }
    }
}
#endif
