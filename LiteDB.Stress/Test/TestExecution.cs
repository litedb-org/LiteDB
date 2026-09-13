using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace LiteDB.Stress
{
    public class TestExecution
    {
        public TimeSpan Duration { get; }
        public Stopwatch Timer { get; } = new Stopwatch();

        private readonly TestFile _file;
        private LiteDatabase _db;
        private bool _running = true;
        private long _maxRam = 0;
        private readonly ConcurrentDictionary<int, ThreadInfo> _threads = new ConcurrentDictionary<int, ThreadInfo>();

        public TestExecution(string filename, TimeSpan duration)
        {
            this.Duration = duration;

            _file = new TestFile(filename);
        }

        public void Execute()
        {
            if (_file.Delete)
            {
                this.DeleteFiles();
            }

            _db = new LiteDatabase(_file.Filename);
            _db.Pragma("TIMEOUT", (int)_file.Timeout.TotalSeconds);

            foreach(var setup in _file.Setup)
            {
                _db.Execute(setup);
            }

            // create all threads
            this.CreateThreads();

            // start report thread
            var t = new Thread(() => this.ReportThread())
            {
                IsBackground = true
            };
            t.Name = "REPORT";
            t.Start();
        }

        private void DeleteFiles()
        {
            var searchPattern = Path.GetFileNameWithoutExtension(_file.Filename);
            var filesToDelete = Directory.GetFiles(".", $"{searchPattern}*" + Path.GetExtension(_file.Filename));

            foreach (var deleteFile in filesToDelete)
            {
                File.Delete(deleteFile);
            }

            File.Delete(_file.Output);
        }

        private void CreateThreads()
        {
            foreach (var task in _file.Tasks)
            {
                for (var i = 0; i < task.TaskCount; i++)
                {
                    var thread = new Thread(() =>
                    {
                        while (true)
                        {
                            var info = _threads[Thread.CurrentThread.ManagedThreadId];
                            info.CancellationTokenSource.Token.WaitHandle.WaitOne(task.Sleep);
                            if (info.CancellationTokenSource.Token.IsCancellationRequested) break;
                            info.Elapsed.Restart();
                            info.Running = true;
                            try
                            {
                                info.Result = task.Execute(_db);
                            }
                            catch (Exception ex)
                            {
                                info.Exception = ex;
                                _running = false;
                                break;
                            }
                            info.Running = false;
                            info.Elapsed.Stop();

                            info.TotalRun += info.Elapsed.Elapsed;

                            if (info.Result.IsInt32) info.ResultSum += (long)info.Result.AsInt32;

                            info.Counter++;
                            info.LastRun = DateTime.Now;
                        }
                    })
                    {
                        IsBackground = true
                    };

                    _threads[thread.ManagedThreadId] = new ThreadInfo
                    {
                        Task = task,
                        Thread = thread
                    };

                    thread.Name = task.Name;
                    thread.Start();
                }
            }
        }

        private void ReportThread()
        {
            this.Timer.Start();

            var output = new StringBuilder();

            while(this.Timer.Elapsed < this.Duration && _running)
            {
                Thread.Sleep(Math.Min(1000, (int)this.Duration.Subtract(this.Timer.Elapsed).TotalMilliseconds));

                this.ReportPrint(output);

                Console.Clear();
                Console.WriteLine(output.ToString());
            }

            this.StopRunning();

            this.Timer.Stop();

            _db.Dispose();

            this.ReportPrint(output);
            this.ReportSummary(output);

            Console.Clear();
            Console.WriteLine(output.ToString());

            File.AppendAllText(_file.Output, output.ToString());
        }

        private void ReportPrint(StringBuilder output)
        {
            output.Clear();

            var process = Process.GetCurrentProcess();

            var ram = process.WorkingSet64 / 1024 / 1024;

            _maxRam = Math.Max(_maxRam, ram);

            output.AppendLine($"LiteDB Multithreaded: {_threads.Count}, running for {this.Timer.Elapsed}");
            output.AppendLine($"Garbage Collector: gen0: {GC.CollectionCount(0)}, gen1: {GC.CollectionCount(1)}, gen2: {GC.CollectionCount(2)}");
            output.AppendLine($"Memory usage: {ram.ToString("n0")} Mb (max: {_maxRam.ToString("n0")} Mb)");
            output.AppendLine();

            foreach (var thread in _threads)
            {
                var howLong = DateTime.Now - thread.Value.LastRun;

                var id = thread.Key.ToString("00");
                var name = (thread.Value.Task.Name + (thread.Value.Running ? "*" : "")).PadRight(15, ' ');
                var counter = thread.Value.Counter.ToString().PadRight(5, ' ');
                var timer = howLong.TotalSeconds > 60 ?
                    ((int)howLong.TotalMinutes).ToString().PadLeft(2, ' ') + " minutes" :
                    ((int)howLong.TotalSeconds).ToString().PadLeft(2, ' ') + " seconds";
                var result = thread.Value.Result != null ? $"[{thread.Value.Result.ToString()}]" : "";
                var running = thread.Value.Elapsed.Elapsed.TotalSeconds > 1 ?
                    $"<LAST RUN {(int)thread.Value.Elapsed.Elapsed.TotalSeconds}s> " :
                    "";
                var ex = thread.Value.Exception != null ?
                    " ERROR: " + thread.Value.Exception.Message :
                    "";

                output.AppendLine($"{id}. {name} :: {counter} >> {timer} {running}{result}{ex}");
            }
        }

        private void ReportSummary(StringBuilder output)
        {
            output.AppendLine("\n=====\n");
            output.AppendLine("Summary Report");
            output.AppendLine();

            foreach(var task in _file.Tasks)
            {
                var name = task.Name.PadRight(15, ' ');
                var count = _threads.Values.Where(x => x.Task == task).Sum(x => (long)x.Counter).ToString().PadLeft(5, ' ');
                var sum = _threads.Values.Where(x => x.Task == task).Sum(x => x.ResultSum);
                var ssum = sum == 0 ? "" : $"[{sum.ToString("n0")}] - ";
                var meanRuntime = TimeSpan.FromMilliseconds(_threads.Values
                    .Where(x => x.Task == task)
                    .Select(x => x.TotalRun.TotalMilliseconds)
                    .Average());

                output.AppendLine($"{name} :: {count} executions >> {ssum}Runtime: {meanRuntime}");
            }
        }

        private void StopRunning()
        {
            foreach (var t in _threads.Values)
            {
                t.CancellationTokenSource.Cancel();
                t.Thread.Join();
            }
        }
    }
}
