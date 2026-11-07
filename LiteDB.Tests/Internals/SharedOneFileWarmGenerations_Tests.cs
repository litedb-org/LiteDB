#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public class SharedOneFileWarmGenerations_Tests
    {
        [OneFileFact]
        public async Task Current_and_retired_warm_generations_keep_exact_frames_and_INCLUDE_through_slot_reuse()
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.SharedGenerationCapacity = 3;
            using var writer = new SharedEngine(settings);
            using var db = new LiteDatabase(writer, disposeOnClose: false);
            db.CheckpointSize = 0;
            var facades = Enumerable.Range(0, 4).Select(_ => new SharedEngine(settings)
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) }).ToArray();
            var streams = new List<IBsonDataReader>();
            var snapshots = new List<CachedSharedSnapshot>();
            var frames = new List<Dictionary<long, byte[]>>();
            var table = writer.MappedReaderTable;
            long releasedSlot = 0, reuseEpoch = 0;
            try
            {
                for (var generation = 0; generation < 4; generation++)
                {
                    var value = generation;
                    await Task.Run(() =>
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("refs").Upsert(new BsonDocument { ["_id"] = 1, ["value"] = value });
                        db.GetCollection("docs").Update(Enumerable.Range(1, 160).Select(id => Document(id, value)));
                        db.Commit().Should().BeTrue();
                    });
                    var facade = facades[generation];
                    using var readerDb = new LiteDatabase(facade, disposeOnClose: false);
                    for (var i = 0; i < 3; i++) readerDb.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(value);
                    var hits = facade.CoordinatedReadHits;
                    streams.Add(await Task.Run(() => facade.Query("docs", new Query { Where = { "value = " + value }, Includes = { "$.ref" } })));
                    facade.CoordinatedReadHits.Should().Be(hits + 1);
                    snapshots.Add(table.ReadCache.Snapshot);
                    var bytes = File.ReadAllBytes(files.Log);
                    frames.Add(snapshots[generation].Engine.GetWalIndex().SnapshotPositions(snapshots[generation].Engine.ReadVersion)
                        .ToDictionary(p => p, p => Frame(bytes, p)));
                    frames[generation].Should().NotBeEmpty();
                    if (generation == 2)
                    {
                        snapshots[0].Retired.Should().BeTrue();
                        snapshots[1].Retired.Should().BeTrue();
                        snapshots[2].Retired.Should().BeFalse();
                        table.LiveVersions().Should().BeEquivalentTo(snapshots.Select(s => (int)s.Status.Version));
                        var slot = Slot(table, (int)snapshots[1].Status.Version);
                        db.Checkpoint();
                        CheckFrames(files, frames[0], frames[1], frames[2]);
                        await Task.Run(() => { streams[1].Dispose(); facades[1].Dispose(); });
                        table.Authority.Map.Load(slot).Should().Be(0);
                        db.Checkpoint();
                        // The next accepted generation must occupy the freed physical record.
                        snapshots[1].Lease.Should().BeNull();
                        frames[1].Clear();
                        releasedSlot = slot;
                        reuseEpoch = table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch);
                    }
                    if (generation == 3)
                    {
                        Slot(table, (int)snapshots[3].Status.Version).Should().Be(releasedSlot);
                        table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch).Should().BeGreaterThan(reuseEpoch,
                            "the write must actually reuse reclaimed physical WAL addresses while older generations remain live");
                    }
                }
                db.Checkpoint();
                CheckFrames(files, frames[0], frames[2], frames[3]);
                table.LiveVersions().Should().BeEquivalentTo(new[] { 0, 2, 3 }.Select(i => (int)snapshots[i].Status.Version));
                foreach (var generation in new[] { 0, 2, 3 })
                {
                    var actual = new List<BsonDocument>();
                    while (streams[generation].Read()) actual.Add(streams[generation].Current.AsDocument);
                    actual.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Included(id, generation)));
                }
                db.GetCollection("docs").Find(Query.EQ("value", 3)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Document(id, 3)));
                db.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
                foreach (var facade in facades) facade.Dispose();
            }
            db.Checkpoint();
            table.HasReaders.Should().BeFalse();
            (File.Exists(files.Log) ? new FileInfo(files.Log).Length : 0).Should().Be(0);
        }

        private static BsonDocument Document(int id, int value)
        {
            var row = Row(id, value);
            row["ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "refs" };
            return row;
        }
        private static BsonDocument Included(int id, int value)
        {
            var row = Document(id, value);
            row["ref"] = new BsonDocument { ["$id"] = 1, ["_id"] = 1, ["value"] = value };
            return row;
        }
        private static byte[] Frame(byte[] bytes, long position) => bytes.Skip(checked((int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize))).Take(Constants.PAGE_SIZE).ToArray();
        private static void CheckFrames(SharedOneFilePublicationFixture files, params Dictionary<long, byte[]>[] generations)
        {
            var bytes = File.ReadAllBytes(files.Log);
            foreach (var generation in generations)
                foreach (var frame in generation) Frame(bytes, frame.Key).Should().Equal(frame.Value, "protected WAL position " + frame.Key);
        }
        private static long Slot(SharedMemoryReaderTable table, int version) => Enumerable.Range(0, table.Authority.Header.Generations)
            .Select(i => table.Authority.Block + SharedMemoryLayout.ReaderOffset + i * 8L)
            .Single(offset => unchecked((int)table.Authority.Map.Load(offset)) == version && table.Authority.Map.Load(offset) != 0);

        [OneFileFact]
        public void Final_unregister_waiting_for_cache_never_holds_the_table_gate()
        {
            var settings = new SharedOneFilePublicationFixture();
            using (settings)
            {
                var table = SharedMemoryReaderTable.Open(settings.Settings);
                var facade = new SharedMemoryReaders(table);
                var lease = table.Register(7);
                facade.Dispose();
                Task close = null;
                try
                {
                    lock (table.ReadCache.Gate)
                    {
                        close = Task.Run(lease.Dispose);
                        SpinWait.SpinUntil(() => Field<int>(table, "_references") == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                        var gate = Field<object>(table, "_gate");
                        var entered = Monitor.TryEnter(gate, TimeSpan.FromSeconds(2));
                        try { entered.Should().BeTrue("final Release must drop every caller-held table gate before taking cache Gate"); }
                        finally { if (entered) Monitor.Exit(gate); }
                        close.IsCompleted.Should().BeFalse("the forced final close is still waiting for cache Gate");
                    }
                }
                finally { close?.Wait(TimeSpan.FromSeconds(5)); lease.Dispose(); }
                table.Closed.Should().BeTrue();
            }
        }
    }
}
#endif
