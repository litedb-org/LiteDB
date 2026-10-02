using System;
using System.Diagnostics;
using System.Threading;
using LiteDB;

/// <summary>Process resource counts: OS threads, OS handles (file descriptors on Unix) and memory.</summary>
internal readonly struct Counters
{
    public readonly int Threads;
    public readonly int Handles;
    public readonly long ManagedBytes;
    public readonly long PrivateBytes;

    private Counters(int threads, int handles, long managed, long privateBytes)
    {
        Threads = threads;
        Handles = handles;
        ManagedBytes = managed;
        PrivateBytes = privateBytes;
    }

    /// <summary>Allocates; never call it inside a timed or allocation-counted interval.</summary>
    public static Counters Sample(bool collect)
    {
        if (collect)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        var managed = GC.GetTotalMemory(collect);
        using var process = Process.GetCurrentProcess();
        return new Counters(process.Threads.Count, process.HandleCount, managed, process.PrivateMemorySize64);
    }

    /// <summary>Sample until the thread and handle counts stop moving (exited threads are reaped lazily).</summary>
    public static Counters Settled()
    {
        var previous = Sample(collect: true);
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(50);
            var current = Sample(collect: true);
            if (current.Threads == previous.Threads && current.Handles == previous.Handles) return current;
            previous = current;
        }
        return previous;
    }

    public JsonRecord ToJson() => new JsonRecord().Add("threads", Threads).Add("handles", Handles)
        .Add("managedBytes", ManagedBytes).Add("privateBytes", PrivateBytes);
}

/// <summary>Candidate-only resource workloads; each asserts its bound and reports the samples.</summary>
internal static class Resources
{
    // Unrelated runtime threads and handles (timers, tiering, the console) may come and go.
    private const int ThreadSlack = 2, HandleSlack = 8;
    // Shared handle holders run on a process-wide pool (#3083): at most this many threads stay idle,
    // each for at most HolderIdleExpiry. Mirrors SharedHolderScheduler; the runner sees only public API.
    private const int HolderIdleThreads = 2;
    private static readonly TimeSpan HolderIdleExpiry = TimeSpan.FromSeconds(1);
    private const long ManagedSlack = 256 * 1024;

    public static bool TryRun(RunSettings settings, JsonRecord record, out bool passed)
    {
        passed = true;
#if HANDLES
        const string Pending = "shared-pending-begins-", Idle = "direct-idle-handles-";
        if (settings.Workload.StartsWith(Pending, StringComparison.Ordinal))
        {
            passed = PendingBegins(settings, record, int.Parse(settings.Workload.Substring(Pending.Length)));
            return true;
        }
        if (settings.Workload.StartsWith(Idle, StringComparison.Ordinal))
        {
            passed = IdleHandles(settings, record, int.Parse(settings.Workload.Substring(Idle.Length)));
            return true;
        }
#endif
        return false;
    }

#if HANDLES
    /// <summary>
    /// One open Shared handle owns the writer; N begins wait behind it, each on its own thread.
    /// At the peak exactly one holder is alive. While the queue drains, at most one busy holder
    /// plus the pool's idle holders are alive, and every count returns to its baseline once all
    /// handles completed and the idle holders expired.
    /// </summary>
    private static bool PendingBegins(RunSettings settings, JsonRecord record, int pending)
    {
        using var db = new LiteDatabase(settings.Connection);
        using (var warm = db.BeginTransaction()) warm.Commit();
        // The warm handle's holder idles in the pool until it expires: measure without it.
        Thread.Sleep(HolderIdleExpiry + TimeSpan.FromMilliseconds(500));
        var before = Counters.Settled();
        int started = 0, acquired = 0;
        var callers = new Thread[pending];
        var errors = new Exception[pending];
        var owner = db.BeginTransaction();
        Workloads.Update(owner.GetCollection("rows"), 1);
        for (var i = 0; i < pending; i++)
        {
            var index = i;
            callers[i] = new Thread(() =>
            {
                try
                {
                    Interlocked.Increment(ref started);
                    using var transaction = db.BeginTransaction();
                    Interlocked.Increment(ref acquired);
                    Workloads.Update(transaction.GetCollection("rows"), index + 2);
                    transaction.Commit();
                }
                catch (Exception error) { errors[index] = error; }
            }) { IsBackground = true };
            callers[i].Start();
        }
        if (!SpinWait.SpinUntil(() => Volatile.Read(ref started) == pending, TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Callers did not start");
        // The public API exposes no waiter count: give the begins time to park on the gate.
        Thread.Sleep(250);
        var parkedWithoutAcquiring = Volatile.Read(ref acquired) == 0;
        var peak = Counters.Sample(collect: true);
        owner.Commit();
        owner.Dispose();
        // While the queue drains, threads that are not live callers are holders (plus runtime
        // slack). Callers only exit, so counting them before each sample never undercounts them.
        var drainPeakThreads = peak.Threads;
        var drainNonCallers = peak.Threads - pending - before.Threads;
        foreach (var caller in callers)
        {
            while (!caller.Join(5))
            {
                var alive = Array.FindAll(callers, c => c.IsAlive).Length;
                var threads = Counters.Sample(collect: false).Threads;
                drainPeakThreads = Math.Max(drainPeakThreads, threads);
                drainNonCallers = Math.Max(drainNonCallers, threads - alive - before.Threads);
            }
        }
        foreach (var error in errors)
            if (error != null) throw new InvalidOperationException("A pending begin failed", error);
        Thread.Sleep(HolderIdleExpiry + TimeSpan.FromMilliseconds(500));
        var after = Counters.Settled();
        var bound = pending + 1 + ThreadSlack;
        var drainBound = bound + HolderIdleThreads;
        var passed = parkedWithoutAcquiring && Volatile.Read(ref acquired) == pending &&
            peak.Threads - before.Threads <= bound && drainPeakThreads - before.Threads <= drainBound &&
            drainNonCallers <= 1 + HolderIdleThreads + ThreadSlack &&
            after.Threads - before.Threads <= ThreadSlack && after.Handles - before.Handles <= HandleSlack &&
            after.ManagedBytes - before.ManagedBytes <= ManagedSlack;
        record.Add("kind", "resource").Add("pending", pending).Add("parked", parkedWithoutAcquiring)
            .Add("before", before.ToJson()).Add("peak", peak.ToJson()).Add("after", after.ToJson())
            .Add("drainPeakThreads", drainPeakThreads).Add("threadBound", bound).Add("drainThreadBound", drainBound)
            .Add("holderIdleThreads", HolderIdleThreads)
            .Add("peakThreadDelta", peak.Threads - before.Threads).Add("drainThreadDelta", drainPeakThreads - before.Threads)
            .Add("drainNonCallerThreads", drainNonCallers)
            .Add("afterThreadDelta", after.Threads - before.Threads).Add("afterHandleDelta", after.Handles - before.Handles)
            .Add("afterManagedDelta", after.ManagedBytes - before.ManagedBytes).Add("passed", passed);
        return passed;
    }

    /// <summary>
    /// K idle Direct handles (begun, no operation) are open at once. Report retained managed bytes
    /// per idle handle, per completed but still referenced handle, and after disposal.
    /// </summary>
    private static bool IdleHandles(RunSettings settings, JsonRecord record, int count)
    {
        using var db = new LiteDatabase(settings.Connection);
        using (var warm = db.BeginTransaction())
        {
            Workloads.Read(warm.GetCollection("rows"), 1);
            warm.Commit();
        }
        var before = Counters.Settled();
        var handles = new ILiteTransaction[count];
        for (var i = 0; i < count; i++) handles[i] = db.BeginTransaction();
        var peak = Counters.Sample(collect: true);
        foreach (var handle in handles) handle.Commit();
        var completed = Counters.Sample(collect: true);
        var committed = Array.TrueForAll(handles, h => h.State == LiteTransactionState.Committed);
        foreach (var handle in handles) handle.Dispose();
        Array.Clear(handles, 0, handles.Length);
        var after = Counters.Settled();
        GC.KeepAlive(handles);
        var passed = committed && after.Threads - before.Threads <= ThreadSlack &&
            after.Handles - before.Handles <= HandleSlack && after.ManagedBytes - before.ManagedBytes <= ManagedSlack;
        record.Add("kind", "resource").Add("handles", count).Add("before", before.ToJson()).Add("peak", peak.ToJson())
            .Add("completed", completed.ToJson()).Add("after", after.ToJson())
            .Add("bytesPerIdleHandle", (peak.ManagedBytes - before.ManagedBytes) / (double)count)
            .Add("bytesPerCompletedHandle", (completed.ManagedBytes - before.ManagedBytes) / (double)count)
            .Add("afterManagedDelta", after.ManagedBytes - before.ManagedBytes)
            .Add("afterThreadDelta", after.Threads - before.Threads).Add("afterHandleDelta", after.Handles - before.Handles)
            .Add("passed", passed);
        return passed;
    }
#endif
}
