using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Measurement probe, not a regression test: runs only with LITEDB_PIN_PROBE set.
    /// An owner thread iterates a leased reader and writes in a tight loop (a pin);
    /// a waiter (another thread of the instance, or another instance) writes too.
    /// Reports the waiter's per-operation wait and the owner's throughput.
    /// </summary>
    public class SharedPinFairness_Probe : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-pinprobe-" + Guid.NewGuid().ToString("N"));

        public SharedPinFairness_Probe(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_directory);
        }

        private static bool Enabled => Environment.GetEnvironmentVariable("LITEDB_PIN_PROBE") != null;

        private static int Setting(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

        [Theory]
        [InlineData("thread")]
        [InlineData("instance")]
        [InlineData("none")]
        [InlineData("plain")]
        [InlineData("plain-instance")]
        public void Waiter_wait_under_a_pinned_write_loop(string waiter)
        {
            if (!Enabled) return;
            var seconds = Setting("LITEDB_PIN_PROBE_SECONDS", 5);
            var burners = Setting("LITEDB_PIN_PROBE_BURNERS", 0);
            var filename = Path.Combine(_directory, waiter + ".db");

            using var engine = new SharedEngine(new EngineSettings { Filename = filename });
            engine.Insert("docs", Enumerable.Range(1, 200).Select(id => Doc(id, 0)), BsonAutoId.Int32);

            using var stop = new CancellationTokenSource();
            var burnerThreads = Enumerable.Range(0, burners).Select(_ => new Thread(() =>
            {
                var x = 0L;
                while (!stop.IsCancellationRequested) x++;
            }) { IsBackground = true, Priority = ThreadPriority.Normal }).ToList();
            burnerThreads.ForEach(t => t.Start());

            var waits = new List<double>();
            var writes = 0;
            string worst = "";
            double worstMs = 0;
            Exception failure = null;
            var plain = waiter.StartsWith("plain");
            var waiterThread = waiter == "none" || waiter == "plain" ? null : new Thread(() =>
            {
                try
                {
                    using var other = waiter.EndsWith("instance") ? new SharedEngine(new EngineSettings { Filename = filename }) : null;
                    var target = other ?? engine;
                    var id = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        var watch = Stopwatch.StartNew();
                        var w0 = Volatile.Read(ref writes);
                        var o0 = engine.EngineOpens;
                        target.Insert("other", new[] { new BsonDocument { ["_id"] = ++id } }, BsonAutoId.Int32);
                        var ms = watch.Elapsed.TotalMilliseconds;
                        lock (waits)
                        {
                            waits.Add(ms);
                            if (ms > worstMs) { worstMs = ms; worst = $"worst={ms:F0}ms ownerWritesDuring={Volatile.Read(ref writes) - w0} opensDuring={engine.EngineOpens - o0}"; }
                        }
                        Thread.Sleep(5);
                    }
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };

            var opens = engine.EngineOpens;
            using (var reader = plain ? null : engine.Query("docs", new Query()))
            {
                reader?.Read();
                engine.Update("docs", new[] { Doc(1, 1) });
                waiterThread?.Start();
                var deadline = Stopwatch.StartNew();
                var value = 2;
                while (deadline.Elapsed.TotalSeconds < seconds)
                {
                    engine.Update("docs", new[] { Doc(1, value++) });
                    Interlocked.Increment(ref writes);
                }
                stop.Cancel();
            }
            waiterThread?.Join(TimeSpan.FromSeconds(60));
            burnerThreads.ForEach(t => t.Join());

            double[] sorted;
            lock (waits) sorted = waits.OrderBy(x => x).ToArray();
            var p = new Func<double, double>(q => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))]);
            _output.WriteLine($"PROBE waiter={waiter} burners={burners} seconds={seconds} ownerWrites/s={writes / (double)seconds:F0} " +
                $"engineOpens={engine.EngineOpens - opens} waiterOps={sorted.Length} p50={p(0.5):F1}ms p99={p(0.99):F1}ms max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F1}ms " +
                $"failure={failure?.GetType().Name}");
            Console.WriteLine($"PROBE waiter={waiter} burners={burners} ownerWrites/s={writes / (double)seconds:F0} engineOpens={engine.EngineOpens - opens} " +
                $"waiterOps={sorted.Length} p50={p(0.5):F1} p99={p(0.99):F1} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F1} {worst} log={LogKb(filename)}KB");
        }

        private static long LogKb(string filename)
        {
            var log = new FileInfo(FileHelper.GetLogFile(filename));
            return log.Exists ? log.Length / 1024 : 0;
        }

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
