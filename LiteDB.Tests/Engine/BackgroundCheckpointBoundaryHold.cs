using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    // Hold only the original worker, once. Close/rebuild's synchronous attempts
    // must never be mistaken for the pass whose lifetime they are joining.
    internal sealed class BackgroundCheckpointBoundaryHold : IDisposable
    {
        internal static readonly string[] Boundaries =
        {
            "before-pass", "before-commit-lock", "before-barrier-lock", "before-index-lock",
            "checkpoint-before-page-write", "checkpoint-after-page-write", "data-page",
            "checkpoint-before-data-flush", "checkpoint-after-data-flush", "data-flushed",
            "before-reclaim", "checkpoint-before-clear", "checkpoint-after-clear", "after-reclaim", "after-pass"
        };

        internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        private int _hits;
        internal int Hits => Volatile.Read(ref _hits);

        internal BackgroundCheckpointBoundaryHold(LiteEngine engine, AutomaticCheckpointCoordinator worker, string boundary)
        {
            void Observe(string stage)
            {
                if (!worker.IsWorkerThread || stage != boundary || Interlocked.Increment(ref _hits) != 1) return;
                Entered.Set();
                AutomaticCheckpointWorker_Tests.Wait(Release);
            }
            worker.BeforeCheckpointWorkerPass = () => Observe("before-pass");
            worker.AfterCheckpointWorkerPass = () => Observe("after-pass");
            engine.CheckpointStage = Observe;
            engine.SimulateCrashPoint = Observe;
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
