using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleReviewSafety_Tests
    {
        public static IEnumerable<object[]> Modes
        {
            get
            {
                yield return new object[] { false, null };
                yield return new object[] { false, "secret" };
#if NET8_0_OR_GREATER
                if (LiteDB.Client.Shared.SharedMemoryAuthority.IsPlatformQualified)
                {
                    yield return new object[] { true, null };
                    yield return new object[] { true, "secret" };
                }
#endif
            }
        }
        // Confirmation can survive process death in the OS cache even before fsync.
        // Index publication can precede the covering group sync, so its cuts are in flight.
        private static readonly Dictionary<string, string> CommitCuts = new Dictionary<string, string>
        {
            ["wal-confirmation-before-write"] = "absent",
            ["wal-confirmation-after-write"] = "atomic",
            ["wal-before-durable-flush"] = "atomic",
            ["wal-after-durable-flush"] = "present",
            ["wal-before-index-confirmation"] = "atomic",
            ["wal-after-index-confirmation"] = "atomic",
            ["handle-before-release"] = "present",
            ["handle-after-release"] = "present",
            ["ack"] = "present"
        };
        public static IEnumerable<object[]> Cuts => Modes.SelectMany(mode => CommitCuts.Select(cut =>
            new object[] { mode[0], mode[1], cut.Key,
                // Legacy and encrypted paths flush before index publication; mapped plain
                // commits publish the index before waiting for their covering group ticket.
                cut.Key.EndsWith("index-confirmation", StringComparison.Ordinal) &&
                    (!(bool)mode[0] || mode[1] != null) ? "present" : cut.Value }));

        [Theory]
        [MemberData(nameof(Cuts))]
        public async Task Shared_handle_commit_death_recovers_atomic_rows_indexes_and_writer(bool mapped, string password, string cut, string expected)
        {
            using var file = new TempFile();
            Seed(file, password);
            var mode = mapped ? "handle-mapped" : "handle-legacy";
            using (var survivor = new HandleProcess(mode, file, password))
            using (var child = new HandleProcess(mode, file, password))
            {
                await survivor.Expect("ready"); await child.Expect("ready");
                child.Send("begin"); await child.Expect("attempting"); await child.Expect("begun");
                child.Send("write"); await child.Expect("written");
                child.Send("cut:" + cut); await child.Expect("cut");
                Assert.Equal(cut == "ack", File.Exists(file.Filename + ".ack"));
                await child.Kill();
                // An already-live facade must recover writer admission, not just a cold process.
                survivor.Send("begin"); await survivor.Expect("attempting"); await survivor.Expect("begun");
                survivor.Send("recover-write"); await survivor.Expect("recovery-written");
                survivor.Send("commit"); await survivor.Expect("committed");
                survivor.Send("close"); await survivor.Expect("done"); await survivor.Finish();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                var rows = db.GetCollection("rows");
                Assert.NotNull(rows.FindById(1));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
                Assert.NotNull(db.GetCollection("recovered").FindById(7));
                var pending = rows.FindById(2);
                if (expected == "absent") Assert.Null(pending);
                else if (expected == "present") Assert.NotNull(pending);
                else Assert.Equal("atomic", expected);
                if (pending != null)
                {
                    Assert.Equal(20, pending["value"].AsInt32);
                    Assert.Equal(new string('x', 50000), pending["payload"].AsString);
                }
                Assert.Null(rows.FindById(0));
                Assert.Equal(pending == null ? 0 : 1, rows.Count(Query.EQ("value", 20)));
                Assert.Equal(pending == null ? 1 : 2, rows.Count());
                db.Checkpoint();
            }
            File.Delete(file.Filename + ".ack");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public async Task Sustained_successor_handles_cannot_starve_pending_process(bool mapped, string password)
        {
            using var file = new TempFile(); Seed(file, password);
            using var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password, SharedOneFile = mapped });
            using var db = new LiteDatabase(engine);
            using var peer = new HandleProcess(mapped ? "handle-mapped" : "handle-legacy", file, password);
            await peer.Expect("ready");
            var locals = Enumerable.Range(0, 2).Select(_ => new LiteDatabase(new SharedEngine(
                new EngineSettings { Filename = file, Password = password, SharedOneFile = mapped }))).ToArray();
            using var holder = db.BeginTransaction();
            peer.Send("compete"); await peer.Expect("waiting");
            var completed = 0;
            var overtakes = 0;
            using var stop = new CancellationTokenSource();
            using var started = new CountdownEvent(2);
            var stream = Enumerable.Range(0, 2).Select(worker => Task.Factory.StartNew(() =>
            {
                using var local = locals[worker];
                started.Signal();
                while (!stop.IsCancellationRequested)
                {
                    using var tx = local.BeginTransaction();
                    tx.GetCollection("stream" + worker).Insert(new BsonDocument { ["n"] = completed });
                    tx.Commit();
                    if (!File.Exists(file.Filename + ".peer-admitted")) Interlocked.Increment(ref overtakes);
                    Interlocked.Increment(ref completed);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                Assert.True(started.Wait(10000));
                holder.Commit();
                await peer.Expect("progress");
#if NET8_0_OR_GREATER
                if (mapped) Assert.InRange(overtakes, 0, LiteDB.Client.Shared.SharedWriterTenure.TransactionBudget);
#endif
                // Keep competing until both local callers have completed real work too.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed) >= 2, 10000));
            }
            finally
            {
                stop.Cancel();
                var all = Task.WhenAll(stream);
                Assert.Same(all, await Task.WhenAny(all, Task.Delay(20000)));
                await all;
            }
            peer.Send("close"); await peer.Expect("done"); await peer.Finish();
            using var verify = db.BeginTransaction();
            Assert.NotNull(verify.GetCollection("peer").FindById(1));
            Assert.Equal(completed, verify.GetCollection("stream0").Count() + verify.GetCollection("stream1").Count());
            verify.Commit();
            File.Delete(file.Filename + ".peer-admitted");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public async Task Actual_shared_maintenance_waits_for_handle_then_rekeys_successfully(bool mapped, string password)
        {
            using var file = new TempFile(); Seed(file, password);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine))
            using (var peer = new LiteDatabase(new SharedEngine(new EngineSettings { Filename = file, Password = password, SharedOneFile = mapped })))
            {
                _ = peer.Timeout;
                using var tx = db.BeginTransaction();
                var bound = tx.GetCollection("rows");
                var deferred = bound.Query().ToEnumerable();
                using var reader = bound.Query().ExecuteReader();
                Assert.True(reader.Read());
                foreach (var facade in new[] { db, peer })
                {
                    var checkpoint = await Task.Run(() => Assert.Throws<LiteException>(() => facade.Checkpoint()));
                    Assert.Equal(120, checkpoint.ErrorCode);
                    var rebuild = await Task.Run(() => Assert.Throws<LiteException>(() => facade.Rebuild(new RebuildOptions { Password = "changed" })));
                    Assert.Equal(120, rebuild.ErrorCode);
                    Assert.Equal(10, bound.FindById(1)["value"].AsInt32);
                }
                reader.Dispose();
                await Task.Run(tx.Commit);
                Assert.Throws<InvalidOperationException>(() => deferred.ToArray());
                peer.Checkpoint();
                db.Dispose(); // Mapped rekey requires the only remaining participant.
                peer.Rebuild(new RebuildOptions { Password = "changed" });
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = "changed" });
                Assert.Equal(1, cold.GetCollection("rows").Count(Query.EQ("value", 10)));
                Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
            }
        }

        private static void Seed(string file, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.Timeout = TimeSpan.FromSeconds(1);
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }
    }
}
