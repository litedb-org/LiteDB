#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmPrefix_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Live_cache_admission_never_leads_two_separate_durable_prefixes_or_a_failed_later_sync(bool fail)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var shared = new LiteDatabase(engine, disposeOnClose: false);
            shared.BeginTrans().Should().BeTrue();
            var core = Core(engine);
            core.Rollback().Should().BeTrue();
            using var db = new LiteDatabase(core, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 0, ["value"] = 0 });
            db.GetCollection("group").EnsureIndex("value");
            db.Checkpoint();
            using var peer = new SharedEngine(files.Settings);
            using var readerDb = new LiteDatabase(peer, disposeOnClose: false);
            var table = engine.MappedReaderTable;
            // Seed the same read-only process cache as protected installation, while
            // main ownership is intentionally retained across the group schedule.
            // All tested admissions below use the public query path and both fences.
            var snapshotSettings = (EngineSettings)typeof(SharedEngine).GetMethod("SnapshotSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(peer, null);
            var snapshotCore = new LiteEngine(snapshotSettings);
            table.Reads.TryRead(out var status).Should().BeTrue();
            table.ReadCache.Snapshot = new CachedSharedSnapshot(snapshotCore, null, status);
            table.ReadCache.IdleLimit = TimeSpan.FromMinutes(1);
            var disk = Field<DiskService>(core, "_disk");
            var state = State(core);
            using var firstSynced = new ManualResetEventSlim();
            using var secondAppended = new ManualResetEventSlim();
            using var resumeFirst = new ManualResetEventSlim();
            using var secondCaptured = new ManualResetEventSlim();
            using var resumeSecond = new ManualResetEventSlim();
            using var published = new ManualResetEventSlim();
            var barriers = 0;
            var captures = 0;
            long firstVersion = -1, secondVersion = -1;
            byte[] durableData = null, durableLog = null, laterLog = null;
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-after-index-confirmation" && firstSynced.IsSet) secondAppended.Set();
                if (stage == "wal-before-durable-flush")
                {
                    var version = Field<WalChecksum>(disk, "_checksums").LastConfirmedPosition / Constants.PAGE_SIZE + 1;
                    if (Interlocked.Increment(ref captures) == 1) firstVersion = version;
                    else
                    {
                        secondVersion = version;
                        laterLog = File.ReadAllBytes(files.Log);
                        secondCaptured.Set();
                        resumeSecond.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        if (fail) throw new IOException("later prefix sync failed");
                    }
                }
                if (stage == "wal-after-durable-flush")
                {
                    if (Interlocked.Increment(ref barriers) != 1) return;
                    durableData = File.ReadAllBytes(files.Filename);
                    durableLog = File.ReadAllBytes(files.Log);
                    firstSynced.Set();
                    resumeFirst.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                }
            };
            Action<string> observe = stage =>
            {
                if (stage != "commit-version") return;
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().BeLessThanOrEqualTo(
                    Volatile.Read(ref barriers) == 1 ? firstVersion : secondVersion);
                published.Set();
            };
            Task first = null, second = null;
            Task<BsonDocument> pending = null;
            try
            {
                first = Start(() => db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 1 }), observe);
                firstSynced.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                AssertInitial(readerDb, peer);
                second = Start(() => db.GetCollection("group").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 2 }), observe);
                secondAppended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                AssertInitial(readerDb, peer);
                resumeFirst.Set();
                secondCaptured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                published.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(firstVersion);
                var hits = peer.CoordinatedReadHits;
                pending = Task.Run(() => readerDb.GetCollection("group").FindById(2));
                SpinWait.SpinUntil(() => Field<int>(peer, "_mutexWaiters") != 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                peer.CoordinatedReadHits.Should().Be(hits, "the old cache cannot admit the newly published prefix");
                resumeSecond.Set();
                first.GetAwaiter().GetResult();
                if (fail) ((Action)(() => second.GetAwaiter().GetResult())).Should().Throw<IOException>();
                else second.GetAwaiter().GetResult();
                if (fail) Fenced(table);
                else table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(secondVersion);
            }
            finally
            {
                resumeFirst.Set(); resumeSecond.Set();
                foreach (var task in new[] { first, second })
                    if (task != null) try { task.GetAwaiter().GetResult(); } catch (IOException) when (fail) { }
                state.AtCrashPoint = null;
                try { shared.Rollback(); } catch (IOException) when (fail) { }
            }
            pending.GetAwaiter().GetResult()["value"].AsInt32.Should().Be(2);
            Verify(shared, 0);
            VerifyDurableImages(files, durableData, durableLog, laterLog, firstVersion);
        }

        private static void AssertInitial(LiteDatabase db, SharedEngine engine)
        {
            var hits = engine.CoordinatedReadHits;
            db.GetCollection("group").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 0, ["value"] = 0 } });
            engine.CoordinatedReadHits.Should().Be(hits + 1, "a real cache admission must run during the delayed prefix");
        }

        private static void VerifyDurableImages(SharedOneFilePublicationFixture files, byte[] data, byte[] prefix, byte[] later, long version)
        {
            var end = checked((int)(version * WalChecksum.FrameSize));
            end.Should().BeLessThan(later.Length);
            var torn = (byte[])later.Clone();
            Array.Copy(prefix, end, torn, end, Math.Min(512, prefix.Length - end));
            var reordered = (byte[])later.Clone();
            Array.Clear(reordered, end, WalChecksum.FrameSize);
            var images = new[] { prefix, torn, reordered };
            for (var fault = 0; fault < images.Length; fault++)
            {
                var path = files.Filename + ".power-" + fault;
                File.WriteAllBytes(path, data);
                File.WriteAllBytes(FileHelper.GetLogFile(path), images[fault]);
                for (var recovery = 0; recovery < 2; recovery++)
                {
                    using var engine = new SharedEngine(new EngineSettings { Filename = path, SharedOneFile = true });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    for (var warm = 0; warm < 3; warm++) db.GetCollection("group").FindById(1)["value"].AsInt32.Should().Be(1);
                    var hits = engine.CoordinatedReadHits;
                    db.GetCollection("group").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 1, ["value"] = 1 } });
                    engine.CoordinatedReadHits.Should().Be(hits + 1);
                    db.GetCollection("group").FindById(2).As<object>().Should().BeNull("lost/torn/reordered unacknowledged bytes cannot become a partial transaction");
                    db.GetCollection("group").FindAll().Should().HaveCount(2);
                    Verify(db, 0);
                    db.Checkpoint();
                }
            }
        }

        private static Task Start(Action action, Action<string> observe) => Task.Factory.StartNew(() =>
        {
            SharedMemoryReaderTable.PublicationStage = observe;
            try { action(); }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
#endif
