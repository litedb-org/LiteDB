using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// Broken states for oracle self-tests that need engine internals (callers outside the
    /// InternalsVisibleTo set, such as LiteDB.Fuzz.Tests, reach them through LiteDB.Fuzz).
    /// </summary>
    internal static class OracleSelfTestHooks
    {
        /// <summary>Count sort spills until the returned scope is disposed.</summary>
        public static IDisposable CountSortSpills(Action onSpill)
        {
            var previous = EngineState.ObserveSortSpill;
            EngineState.ObserveSortSpill = _ => onSpill();
            return new Restore(() => EngineState.ObserveSortSpill = previous);
        }

        /// <summary>The broken state of the ownership self-test: give up the writer mutex mid-operation.</summary>
        public static void ReleaseOwnershipEarly(SharedEngine connection)
        {
            connection.MutexOwner.ReleaseAll();
            connection.MutexOwner.WaitForRelease();
        }

        private sealed class Restore : IDisposable
        {
            private Action _restore;
            public Restore(Action restore) => _restore = restore;
            public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
        }
    }
}
