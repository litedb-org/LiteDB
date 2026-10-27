using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Infrastructure outlives its opening call and must never capture request state.
    internal static class SharedBackground
    {
        internal static Timer CreateTimer(TimerCallback callback, object state, TimeSpan due, TimeSpan period)
        {
            if (ExecutionContext.IsFlowSuppressed()) return new Timer(callback, state, due, period);
            using (ExecutionContext.SuppressFlow()) return new Timer(callback, state, due, period);
        }

        internal static void Start(Thread thread)
        {
            if (ExecutionContext.IsFlowSuppressed()) thread.Start();
            else using (ExecutionContext.SuppressFlow()) thread.Start();
        }
    }
}
