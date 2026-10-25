#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Internals
{
    /// <summary>
    /// Death of a process that owns a Shared transaction handle: parties already waiting for its
    /// native writer ownership proceed, and random kill points in a loop of acknowledged commits
    /// never lose an acknowledged commit or expose a gap.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleProcessDeath_Tests
    {
        /// <summary>Rounds per kill-loop configuration; override with LITEDB_HANDLE_KILL_ROUNDS.</summary>
        private const int DefaultKillRounds = 8;
        private const int HandleStart = 100, LegacyStart = 100100;
        private readonly ITestOutputHelper _output;

        public TransactionHandleProcessDeath_Tests(ITestOutputHelper output) => _output = output;

        public enum Waiter { LocalOrdinary, LocalBegin, OtherProcessBegin }

        public enum LoopConfig { HandlePlain, HandleEncrypted, Legacy, HandleVersusLegacy }

        [Theory]
        [InlineData(Waiter.LocalOrdinary, null)]
        [InlineData(Waiter.LocalOrdinary, "secret")]
        [InlineData(Waiter.LocalBegin, null)]
        [InlineData(Waiter.LocalBegin, "secret")]
        [InlineData(Waiter.OtherProcessBegin, null)]
        [InlineData(Waiter.OtherProcessBegin, "secret")]
        public async Task Waiters_alive_at_handle_owner_death_proceed(Waiter waiter, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            // The holder's handle inserted id 2 (spilled to the WAL) and owns the native writer mutex.
            using var holder = new MvccProcess("handle-hold", file, password, "shared");
            await holder.Expect("ready");
            using var probe = new SharedEngine(new EngineSettings { Filename = file, Password = password });
            var turnstile = (SharedMutexTurnstile)typeof(SharedEngine)
                .GetField("_turnstile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(probe);
            var shared = new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Shared };
            using var db = new LiteDatabase(shared);
            MvccProcess writer = null;
            Task pending;
            int waiterId;
            try
            {
                switch (waiter)
                {
                    case Waiter.LocalOrdinary:
                        waiterId = 3;
                        pending = Task.Run(() => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 300 }));
                        break;
                    case Waiter.LocalBegin:
                        waiterId = 4;
                        pending = Task.Run(() =>
                        {
                            using var tx = db.BeginTransaction();
                            Assert.Null(tx.GetCollection("rows").FindById(2));
                            tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["value"] = 400 });
                            tx.Commit();
                        });
                        break;
                    default:
                        waiterId = 3; // handle-writer inserts id 3
                        writer = new MvccProcess("handle-writer", file, password, "shared");
                        await writer.Expect("begin");
                        pending = writer.Expect("done");
                        break;
                }
                // Barrier: the waiter is queued at the turnstile for the owner's native mutex.
                Assert.True(SpinWait.SpinUntil(turnstile.HasWaiter, TimeSpan.FromSeconds(20)), "The waiter never queued for the owner's mutex.");
                Assert.False(pending.IsCompleted, "The waiter acquired while the handle owner was alive.");
                await holder.Kill();
                await pending.WaitAsync(TimeSpan.FromSeconds(30));
                if (writer != null) await writer.Finish();
            }
            finally { writer?.Dispose(); }
            db.Dispose();
            probe.Dispose();
            var expected = new[] { 1, waiterId };
            using (var reopened = new LiteDatabase(shared)) AssertModel(reopened, expected);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var direct = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                AssertModel(direct, expected);
            }
        }

        [Theory]
        [InlineData(LoopConfig.HandlePlain)]
        [InlineData(LoopConfig.HandleEncrypted)]
        [InlineData(LoopConfig.Legacy)]
        [InlineData(LoopConfig.HandleVersusLegacy)]
        public async Task Random_kill_points_preserve_every_acknowledged_commit(LoopConfig config)
        {
            var rounds = int.TryParse(Environment.GetEnvironmentVariable("LITEDB_HANDLE_KILL_ROUNDS"), out var configured) && configured > 0
                ? configured : DefaultKillRounds;
            var password = config == LoopConfig.HandleEncrypted ? "secret" : null;
            var modes = config == LoopConfig.Legacy ? new[] { "legacy-loop" }
                : config == LoopConfig.HandleVersusLegacy ? new[] { "handle-loop", "legacy-loop" } : new[] { "handle-loop" };
            var random = new Random(3064 + (int)config);
            var failures = new List<string>();
            for (var round = 0; round < rounds; round++)
            {
                using var file = new TempFile();
                Seed(file, password);
                var acks = new int[modes.Length];
                var killAfter = modes.Select(_ => random.Next(0, 12)).ToArray();
                var jitter = modes.Select(_ => random.Next(0, 200_000)).ToArray();
                var order = modes.Length == 2 && random.Next(2) == 1 ? new[] { 1, 0 } : Enumerable.Range(0, modes.Length).ToArray();
                var processes = modes.Select(mode => new MvccProcess(mode, file, password, "shared")).ToArray();
                try
                {
                    foreach (var process in processes) await process.Expect("ready");
                    var readers = processes.Select((process, n) => Task.Run(async () =>
                    {
                        while (true)
                        {
                            string line;
                            try { line = await process.ReadLine(TimeSpan.FromSeconds(60)); } catch { return; }
                            if (line == null) return;
                            if (line.StartsWith("ack ")) Volatile.Write(ref acks[n], int.Parse(line.Substring(4)));
                        }
                    })).ToArray();
                    foreach (var n in order)
                    {
                        // Kill point: after the writer acknowledged a random number of commits, plus
                        // a random spin into its next commit.
                        var start = modes[n] == "legacy-loop" ? LegacyStart : HandleStart;
                        Assert.True(SpinWait.SpinUntil(() => killAfter[n] == 0 || Volatile.Read(ref acks[n]) >= start + killAfter[n] - 1,
                            TimeSpan.FromSeconds(60)), $"{modes[n]} acknowledged nothing for 60 s");
                        Thread.SpinWait(jitter[n]);
                        await processes[n].Kill();
                    }
                    await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally { foreach (var process in processes) process.Dispose(); }

                // Recover through a Shared open first (abandoned mutex, WAL recovery), then twice directly.
                using (var shared = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                    shared.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 10 + round });
                string previous = null;
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    var all = db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                    var indexed = db.GetCollection("rows").Find(Query.GTE("value", 0)).Count();
                    var last = db.GetCollection("rows").FindById(1)["last"];
                    var ok = indexed == all.Length && all.Contains(1) &&
                        db.GetCollection("sentinel").FindById(9) != null && db.GetCollection("sentinel").FindById(10 + round) != null;
                    var maxima = new List<int>();
                    var state = $"round {round} kill={string.Join("/", killAfter)} jitter={string.Join("/", jitter)} order={string.Join("", order)}";
                    for (var n = 0; n < modes.Length; n++)
                    {
                        var start = modes[n] == "legacy-loop" ? LegacyStart : HandleStart;
                        var ids = all.Where(id => start == HandleStart ? id >= HandleStart && id < LegacyStart : id >= LegacyStart).ToArray();
                        var ack = Volatile.Read(ref acks[n]);
                        // Acknowledged commits are a subset of the recovered contiguous prefix, with at
                        // most one acknowledged-but-unreported extra commit.
                        var prefix = ids.SequenceEqual(Enumerable.Range(start, ids.Length));
                        var max = ids.Length == 0 ? start - 1 : ids.Max();
                        var covers = ack == 0 ? max <= start : max >= ack && max <= ack + 1;
                        ok &= prefix && covers;
                        if (ids.Length > 0) maxima.Add(max);
                        state += $" {modes[n]}: ack={ack} n={ids.Length} max={max} prefix={prefix}";
                    }
                    ok &= maxima.Count == 0 ? last.IsNull : maxima.Contains(last.AsInt32);
                    state += $" indexed={indexed}/{all.Length} last={last}";
                    var image = string.Join(",", all) + "|" + last;
                    if (previous != null && previous != image) { ok = false; state += " (changed between reopens)"; }
                    previous = image;
                    _output.WriteLine($"{config} {state} ok={ok}");
                    if (!ok) failures.Add(state);
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private static void Seed(string file, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }

        private static void AssertModel(LiteDatabase db, int[] ids)
        {
            Assert.Equal(ids, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            Assert.Equal(ids.Length, db.GetCollection("rows").Find(Query.GTE("value", 0)).Count());
            Assert.Null(db.GetCollection("rows").FindById(2));
            Assert.NotNull(db.GetCollection("sentinel").FindById(9));
        }
    }
}
#endif
