using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    /// <summary>
    /// Async implementation of ManualResetEvent
    /// https://devblogs.microsoft.com/pfxteam/building-async-coordination-primitives-part-1-asyncmanualresetevent/
    /// </summary>
    internal class AsyncManualResetEvent
    {
        private volatile TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        public Task WaitAsync()
        {
            return _tcs.Task;
        }

        public void Set()
        {
            _tcs.TrySetResult(true);
        }

        public void Reset()
        {
            while (true)
            {
                var tcs = _tcs;
                if (!tcs.Task.IsCompleted ||
                    Interlocked.CompareExchange(ref _tcs, new TaskCompletionSource<bool>(), tcs) == tcs)
                    return;
            }
        }
    }
}