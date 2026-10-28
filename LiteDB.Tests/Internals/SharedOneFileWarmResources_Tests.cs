#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmResources_Tests
    {
        [OneFileTheory]
        [InlineData(100, false)]
        [InlineData(101, true)]
        public void Value_and_byte_boundaries_execute_once_and_preserve_streaming_prefix(int count, bool streamed)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            // Project away payload so only the value budget distinguishes these rows.
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var hits = engine.CoordinatedReadHits;
            using (var reader = engine.Query("docs", new Query { Limit = count, Select = "{_id: _id}" }))
            {
                engine.MappedReaderTable.HasReaders.Should().Be(streamed);
                var ids = new List<int>();
                do
                {
                    if (!reader.Read()) break;
                    ids.Add(reader.Current["_id"].AsInt32);
                } while (true);
                ids.Should().Equal(Enumerable.Range(1, count));
            }
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            // Full fixture rows cross 64 KiB before the 100-value limit.
            var fitting = 0;
            for (var bytes = 0; fitting < 100; fitting++)
            {
                bytes += Row(fitting + 1, 0).GetBytesCount(true);
                if (bytes > 64 * 1024) break;
            }
            for (var extra = 0; extra <= 1; extra++)
            {
                using var reader = engine.Query("docs", new Query { Limit = fitting + extra });
                engine.MappedReaderTable.HasReaders.Should().Be(extra != 0);
                var rows = new List<BsonDocument>();
                while (reader.Read()) rows.Add(reader.Current.AsDocument);
                rows.Should().BeEquivalentTo(Enumerable.Range(1, fitting + extra).Select(id => Row(id, 0)));
            }
        }

        [OneFileFact]
        public void Expiry_and_actual_sort_spill_release_idle_engine_and_owned_buffers()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var snapshot = engine.MappedReaderTable.ReadCache.Snapshot;
            var core = snapshot.Engine;
            Field<SortDisk>(core, "_sortDisk").Dispose();
            var settings = Field<EngineSettings>(core, "_settings");
            var header = Field<HeaderPage>(core, "_header");
            var temp = new TempStream(files.Filename + "-sort", maxMemoryUsage: 0);
            var sort = new SortDisk(new StreamFactory(temp, settings.Password, ownsStream: true), Constants.PAGE_SIZE, header.Pragmas);
            typeof(LiteEngine).GetField("_sortDisk", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(core, sort);
            db.GetCollection("docs").Query().OrderByDescending("SUBSTRING(payload,0,200)").ToArray().Length.Should().Be(160);
            sort.HasSpilled.Should().BeTrue();
            engine.HasCachedSnapshot.Should().BeFalse();
            temp.CanRead.Should().BeFalse();
            File.Exists(temp.Filename).Should().BeFalse();
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var cache = engine.MappedReaderTable.ReadCache;
            cache.Snapshot.Lease.Should().BeNull();
            cache.IdleLimit = TimeSpan.Zero;
            cache.Expire();
            engine.HasCachedSnapshot.Should().BeFalse();
            cache.Idle.Should().BeNull();
            engine.MappedReaderTable.HasReaders.Should().BeFalse();
            Verify(db, 0);
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Independent_four_MiB_WAL_and_page_cache_limits_prevent_idle_retention(bool largeWal)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("large").InsertBulk(Enumerable.Range(0, 600).Select(id =>
                new BsonDocument { ["_id"] = id, ["payload"] = new string('b', 7500) }));
            if (!largeWal) db.Checkpoint();
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1);
            if (largeWal)
            {
                new FileInfo(files.Log).Length.Should().BeGreaterThan(4 * 1024 * 1024);
                engine.HasCachedSnapshot.Should().BeFalse();
            }
            else
            {
                engine.HasCachedSnapshot.Should().BeTrue();
                var core = engine.MappedReaderTable.ReadCache.Snapshot.Engine;
                var cache = Field<DiskService>(core, "_disk").Cache;
                using (var reader = engine.Query("large", new Query()))
                {
                    var count = 0;
                    while (reader.Read())
                    {
                        if (++count == 600) cache.AllocatedBytes.Should().BeGreaterThan(4 * 1024 * 1024);
                    }
                    count.Should().Be(600);
                }
                engine.HasCachedSnapshot.Should().BeFalse();
                cache.PagesInUse.Should().Be(0);
            }
        }

        [OneFileFact]
        public void Process_pacing_rechecks_disposal_after_pause_and_stale_requests_expire()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var now = Environment.TickCount64;
            var old = table.Reads.RequestWriterTurn(now);
            var newer = table.Reads.RequestWriterTurn(now);
            table.Reads.EndWriterTurn(old);
            table.Reads.ShouldYieldToWriter(now).Should().BeTrue();
            table.Reads.ShouldYieldToWriter(now + 101).Should().BeFalse();
            table.Reads.EndWriterTurn(newer);
            table.Reads.ShouldYieldToWriter(now).Should().BeFalse();
            var pacer = table.ReadCache.Pacer;
            pacer.RecordWork(Stopwatch.Frequency);
            pacer.ReserveDelay(true).Should().Be(10);
            engine.ForceCoordinatedYield = true;
            var reached = 0;
            engine.CoordinationStage = stage =>
            {
                if (stage != "writer-pressure") return;
                table.HasReaders.Should().BeFalse("pacing must precede record publication");
                reached++;
                engine.Dispose();
            };
            Action read = () => db.GetCollection("docs").FindById(1);
            read.Should().Throw<ObjectDisposedException>();
            reached.Should().Be(1);
            table.Closed.Should().BeTrue();
        }

        [OneFileFact]
        public void Active_generation_never_expires_and_warm_capacity_failure_releases_slow_ownership()
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.SharedGenerationCapacity = 1;
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var cache = table.ReadCache;
            using (var reader = engine.Query("docs", new Query()))
            {
                var accepted = cache.Snapshot;
                cache.IdleLimit = TimeSpan.FromMilliseconds(10);
                accepted.LastUse = Environment.TickCount64 - 1000;
                cache.Expire();
                cache.Snapshot.Should().BeSameAs(accepted);
                table.HasReaders.Should().BeTrue();
                var rows = new List<BsonDocument>();
                while (reader.Read()) rows.Add(reader.Current.AsDocument);
                rows.Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                cache.IdleLimit = TimeSpan.FromMinutes(1);
            }
            using (var full = table.Register(123))
            {
                Action read = () => db.GetCollection("docs").FindById(1);
                read.Should().Throw<SharedGenerationCapacityException>();
                engine.MutexOwner.WaitForRelease();
                engine.MutexOwner.IsHeld.Should().BeFalse();
            }
            Verify(db, 0);
        }

        [OneFileFact]
        public void Capacity_refusal_precedes_callback_and_a_stateful_callback_is_never_replayed()
        {
            using var files = new SharedOneFilePublicationFixture();
            var calls = 0;
            var settings = files.Settings;
            settings.SharedGenerationCapacity = 1;
            settings.ReadTransform = (_, value) => { calls++; return value; };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using (var full = engine.MappedReaderTable.Register(123))
            {
                Action read = () => engine.Query("docs", new Query());
                read.Should().Throw<SharedGenerationCapacityException>();
                calls.Should().Be(0);
            }
            db.GetCollection("docs").FindAll().ToArray().Should().HaveCount(160);
            calls.Should().Be(160);
            engine.MappedReaderTable.HasReaders.Should().BeFalse();
        }
    }
}
#endif
