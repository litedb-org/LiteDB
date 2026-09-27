using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace LiteDB.Fuzz;

internal static class FuzzThread
{
    // Do not retain the Thread in the caller: retirement/GC is part of the input
    // scenario, and retaining it would hide numeric thread-ID reuse.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Fuzz thread did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void CollectRetiredThreads()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
