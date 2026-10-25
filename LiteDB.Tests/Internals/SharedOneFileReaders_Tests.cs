#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedOneFileReaders_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shm-readers-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedOneFileReaders_Tests() => Directory.CreateDirectory(_directory);
        private EngineSettings Settings(int generations = 256) => new EngineSettings
        { Filename = Filename, SharedOneFile = true, SharedGenerationCapacity = generations };

        [OneFileFact]
        public async Task All_256_exact_generations_version_zero_duplicates_churn_and_off_thread_double_release()
        {
            var table = SharedMemoryReaderTable.Open(Settings());
            var facade = new SharedMemoryReaders(table);
            var leases = Enumerable.Range(0, 256).Select(facade.Register).ToArray();
            using var duplicate = facade.Register(0);
            table.LiveVersions().Should().BeEquivalentTo(Enumerable.Range(0, 256));
            Action full = () => facade.Register(256);
            full.Should().Throw<SharedGenerationCapacityException>();
            for (var i = 255; i >= 0; i -= 2) leases[i].Dispose();
            var replacements = Enumerable.Range(1000, 128).Select(facade.Register).ToArray();
            table.LiveVersions().Should().BeEquivalentTo(Enumerable.Range(0, 128).Select(i => i * 2).Concat(Enumerable.Range(1000, 128)));
            facade.Dispose(); // Results keep the actual slot/mapping/admission alive.
            table.Closed.Should().BeFalse();
            await MvccProcess.Run("direct-host-direct-refused", Filename, null);
            await Task.Run(() => { foreach (var lease in leases.Concat(replacements)) { lease.Dispose(); lease.Dispose(); } });
            table.LiveVersions().Should().Equal(new[] { 0 });
            duplicate.Dispose();
            table.Closed.Should().BeTrue();
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [OneFileFact]
        public void Malformed_live_record_and_slot_identity_return_unknown_not_an_empty_floor_set()
        {
            var table = SharedMemoryReaderTable.Open(Settings());
            using var facade = new SharedMemoryReaders(table);
            using var zero = facade.Register(0);
            var offset = table.Authority.Block + SharedMemoryLayout.ReaderOffset;
            var valid = table.Authority.Map.Load(offset);
            valid.Should().NotBe(0);
            table.Authority.Map.Store(offset, 123);
            Assert.Null(table.LiveVersions());
            table.Authority.Map.Store(offset, valid);
            table.LiveVersions().Should().Equal(0);
            table.Authority.Map.Store(table.Authority.Block, 0);
            Assert.Null(table.LiveVersions());
            table.Authority.Map.Store(table.Authority.Block, 1);
            table.LiveVersions().Should().Equal(0);
        }

        [OneFileFact]
        public async Task Paused_reader_stays_live_killed_records_disappear_and_slot_reuse_changes_incarnation()
        {
            using var first = new MvccProcess("one-file-reader-hold", Filename, null);
            await first.Expect("ready");
            var firstIdentity = await first.ReadLine(TimeSpan.FromSeconds(5));
            var table = SharedMemoryReaderTable.Open(Settings());
            using var local = new SharedMemoryReaders(table);
            table.LiveVersions().Should().Equal(11);
            kill(first.ProcessId, 19).Should().Be(0);
            try { table.LiveVersions().Should().Equal(11); }
            finally { kill(first.ProcessId, 18).Should().Be(0); }
            await first.Kill();
            table.LiveVersions().Should().BeEmpty();
            using var second = new MvccProcess("one-file-reader-hold", Filename, null);
            await second.Expect("ready");
            var secondIdentity = await second.ReadLine(TimeSpan.FromSeconds(5));
            firstIdentity.Split(':')[0].Should().Be(secondIdentity.Split(':')[0]);
            firstIdentity.Should().NotBe(secondIdentity);
            table.LiveVersions().Should().Equal(11);
            await second.Finish(release: true);
            table.LiveVersions().Should().BeEmpty();
        }

        [OneFileFact]
        public void Three_live_snapshot_generations_keep_complete_payloads_indexes_and_unrelated_collection_through_checkpoint()
        {
            using (var seed = new LiteDatabase(Filename))
            {
                seed.GetCollection("docs").Insert(Documents(0));
                seed.GetCollection("docs").EnsureIndex("group");
                seed.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 9, ["value"] = "untouched" });
            }
            using var engine = new SharedEngine(Settings());
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var readers = new List<IBsonDataReader>();
            try
            {
                for (var generation = 0; generation < 3; generation++)
                {
                    var reader = engine.Query("docs", new Query { Where = { "group = 1" } });
                    reader.Read().Should().BeTrue();
                    reader.Current["generation"].AsInt32.Should().Be(generation);
                    readers.Add(reader);
                    db.GetCollection("docs").Update(Documents(generation + 1));
                    db.Checkpoint();
                }
                for (var generation = 0; generation < readers.Count; generation++)
                {
                    var count = 1;
                    while (readers[generation].Read())
                    {
                        readers[generation].Current["generation"].AsInt32.Should().Be(generation);
                        readers[generation].Current["payload"].AsString.Should().Be(new string((char)('a' + generation), 2048));
                        count++;
                    }
                    count.Should().Be(150);
                }
                db.GetCollection("docs").Find(Query.EQ("group", 1)).Select(d => d["generation"].AsInt32).Should().OnlyContain(g => g == 3);
                db.GetCollection("unrelated").FindById(9)["value"].AsString.Should().Be("untouched");
            }
            finally { foreach (var reader in readers) reader.Dispose(); }
            db.Checkpoint();
            Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName)
                .Should().OnlyContain(name => name == "test.db" || name == "test.db-log" || name == "test.db-shm");
            engine.CoordinatedReadHits.Should().Be(0, "warm mapped admission belongs to slice 6");
        }

        [OneFileFact]
        public async Task Generation_capacity_fails_before_stateful_query_callback_executes()
        {
            using (var seed = new LiteDatabase(Filename)) seed.GetCollection("docs").Insert(Documents(0));
            var callbacks = 0;
            var settings = Settings(1);
            settings.ReadTransform = (collection, document) => { callbacks++; return document; };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var first = engine.Query("docs", new Query());
            first.Read().Should().BeTrue();
            await Task.Run(() => db.GetCollection("docs").Update(Documents(1)));
            var before = callbacks;
            Action next = () => { using var reader = engine.Query("docs", new Query()); };
            next.Should().Throw<SharedGenerationCapacityException>();
            callbacks.Should().Be(before);
        }

        [OneFileFact]
        public void New_and_legacy_shared_protocols_cannot_mix_locally()
        {
            using var engine = new SharedEngine(Settings());
            Action old = () => { using var legacy = new SharedEngine(new EngineSettings { Filename = Filename }); };
            old.Should().Throw<IOException>().WithMessage("*admission*");
        }

        private static IEnumerable<BsonDocument> Documents(int generation) => Enumerable.Range(0, 300).Select(id =>
            new BsonDocument { ["_id"] = id, ["group"] = id % 2, ["generation"] = generation,
                ["payload"] = new string((char)('a' + generation), 2048) });
        [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
