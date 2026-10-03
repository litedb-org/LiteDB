using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>A recorded execution of a case: the history and the final committed contents.</summary>
    public sealed class ExecutionHistory
    {
        internal ExecutionHistory(IReadOnlyList<OperationRecord> records, string[] finalContents, string failure)
        {
            this.Records = records;
            this.FinalContents = finalContents;
            this.Failure = failure;
        }

        public IReadOnlyList<OperationRecord> Records { get; }
        public string[] FinalContents { get; }

        /// <summary>Liveness or harness failure (a thread that never finished, a probe that hung); null if complete.</summary>
        public string Failure { get; }

        /// <summary>The synchronization boundaries every parallel execution uses.</summary>
        public const string Boundaries =
            "boundaries: the prefix ran on T0 and was joined; suffix threads T1..Tn were started, then released together by one " +
            "start event; the logical clock ticks before each call and after its return; after every suffix thread signalled " +
            "completion (threads kept alive) a probe thread wrote and read every collection for the final contents";

        /// <summary>Every record by start order (thread, index, [start-end], command, result), then the final contents.</summary>
        public string Describe() =>
            "    " + Boundaries + Environment.NewLine +
            string.Join(Environment.NewLine, this.Records.OrderBy(r => r.Start).Select(r => "    " + r)) +
            (this.FinalContents == null ? "" : Environment.NewLine + "    final: " + string.Join(" ", this.FinalContents.Select((c, i) => "c" + i + "=" + c)));
    }

    /// <summary>
    /// Runs a case against a fresh real database: the prefix on its own thread, then every suffix on
    /// a dedicated thread released together, recording each command's result between two ticks of
    /// a shared logical clock. When every thread finished (threads stay alive meanwhile), a probe
    /// writes every collection and reads the final committed contents.
    /// </summary>
    public static class ParallelExecutor
    {
        public static ExecutionHistory Run(PropertyCase propertyCase, PropertyOptions options)
        {
            var recorder = new HistoryRecorder();
            var shared = new ConcurrentDictionary<int, object>();
            var database = new PropertyDatabase(options);
            var disposeDatabase = true;
            var start = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var done = new CountdownEvent(propertyCase.Suffixes.Count);
            var workers = new List<Thread>();
            try
            {
                var prefix = StartWorker(0, propertyCase, database, shared, recorder, null, null, null);
                if (!prefix.Join(options.HangDeadline))
                {
                    disposeDatabase = false;
                    return Hang(recorder, "the sequential prefix did not finish");
                }

                for (var t = 1; t < propertyCase.Threads; t++)
                    workers.Add(StartWorker(t, propertyCase, database, shared, recorder, start, done, release));
                start.Set();

                if (!done.Wait(options.HangDeadline))
                {
                    disposeDatabase = false;
                    return Hang(recorder, $"suffix threads did not finish within {options.HangDeadline.TotalSeconds:0}s");
                }

                string[] final = null;
                Exception probeError = null;
                var probe = new Thread(() =>
                {
                    try { final = database.ProbeAndRead(propertyCase.Collections); }
                    catch (Exception ex) { probeError = ex; }
                }) { IsBackground = true, Name = "pbt-probe" };
                probe.Start();
                if (!probe.Join(options.ProbeDeadline))
                {
                    disposeDatabase = false;
                    return new ExecutionHistory(recorder.Snapshot(), null,
                        "PROBE-HANG: every thread completed its commands, yet a write on another thread did not complete within " +
                        $"{options.ProbeDeadline.TotalSeconds:0}s (ownership retained by an idle thread)");
                }
                if (probeError != null)
                {
                    return new ExecutionHistory(recorder.Snapshot(), null,
                        "PROBE-FAILED: after every thread completed its commands a write on another thread failed: " + probeError.GetType().Name + ": " + probeError.Message);
                }
                return new ExecutionHistory(recorder.Snapshot(), final, null);
            }
            finally
            {
                release.Set();
                foreach (var worker in workers) worker.Join(TimeSpan.FromSeconds(5));
                if (disposeDatabase) database.Dispose();
                else DisposeInBackground(database);
            }
        }

        private static Thread StartWorker(int thread, PropertyCase propertyCase, PropertyDatabase database,
            ConcurrentDictionary<int, object> shared, HistoryRecorder recorder, ManualResetEvent start, CountdownEvent done, ManualResetEvent release)
        {
            var worker = new Thread(() =>
            {
                var context = new ThreadContext(thread, database.Database, shared);
                start?.WaitOne();
                foreach (var command in propertyCase.Commands(thread)) recorder.Execute(command, context);
                done?.Signal();
                // Stay alive until the probe ran: thread exit would release a retained Shared mutex.
                release?.WaitOne();
            })
            { IsBackground = true, Name = "pbt-T" + thread };
            worker.Start();
            return worker;
        }

        private static ExecutionHistory Hang(HistoryRecorder recorder, string what) =>
            // A call that never returned may have had its complete effect or none: it is recorded as uncertain.
            new ExecutionHistory(recorder.Snapshot(), null, "HANG: " + what + "; still running: " + string.Join(", ", recorder.Running()));

        private static void DisposeInBackground(PropertyDatabase database)
        {
            // A hung case may keep the engine busy forever; do not block the runner on it.
            new Thread(() =>
            {
                try { database.Dispose(); }
                catch { }
            }) { IsBackground = true, Name = "pbt-dispose" }.Start();
        }
    }
}
