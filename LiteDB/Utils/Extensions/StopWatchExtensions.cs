using System;
using System.Diagnostics;

namespace LiteDB.Utils.Extensions
{
    public static class StopWatchExtensions
    {
        // Start the stopwatch and returns an IDisposable that will stop the stopwatch when disposed
        public static IDisposable StartDisposable(this Stopwatch stopwatch)
        {
            stopwatch.Start();
            return new DisposableAction(stopwatch.Stop);
        }

        private class DisposableAction : IDisposable
        {
            private readonly Action _action;

            public DisposableAction(Action action)
            {
                _action = action;
            }

            public void Dispose()
            {
                _action();
            }
        }
    }

}