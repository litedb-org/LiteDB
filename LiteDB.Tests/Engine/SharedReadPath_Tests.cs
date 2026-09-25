using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Shared-mode read path: an operation opens a fresh engine, so its fixed costs
    /// decide read latency. These tests pin the safety of the shortcuts taken there.
    /// </summary>
    public class SharedReadPath_Tests : IDisposable
    {
        private const int Rows = 300; // more than the buffered budget, so scans lease a snapshot
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-readpath-" + Guid.NewGuid().ToString("N"));

        public SharedReadPath_Tests() => Directory.CreateDirectory(_directory);

        private string Filename => Path.Combine(_directory, "test.db");

        private SharedEngine Open(bool autoRebuild = false) => new SharedEngine(new EngineSettings
        {
            Filename = this.Filename,
            AutoRebuild = autoRebuild
        });

        private static BsonDocument Doc(int id, int value = 0) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 400) };

        private void Seed()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = this.Filename });
            db.GetCollection("docs").InsertBulk(Enumerable.Range(1, Rows).Select(id => Doc(id)));
        }

        private void MarkInvalidState()
        {
            using var stream = new FileStream(this.Filename, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[Constants.PAGE_SIZE];
            stream.Read(header, 0, header.Length);
            header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            PageChecksum.Write(new BufferSlice(header, 0, Constants.PAGE_SIZE));
            stream.Position = 0;
            stream.Write(header, 0, header.Length);
        }

        private bool Rebuilt() => Directory.GetFiles(_directory).Any(f => Path.GetFileName(f).Contains("-backup"));

        [Fact]
        public void Auto_rebuild_waits_for_a_live_snapshot_reader_and_runs_once_it_closed()
        {
            this.Seed();
            using var writer = this.Open(autoRebuild: true);
            using (var readerConnection = this.Open())
            using (var reader = readerConnection.Query("docs", new Query()))
            {
                reader.Read().Should().BeTrue("the scan exceeds the buffer and leases a snapshot");
                this.MarkInvalidState();

                // The rebuild would replace the files under the leased reader.
                new LiteDatabase(writer, disposeOnClose: false).GetCollection("docs").Count().Should().Be(Rows);
                this.Rebuilt().Should().BeFalse("a live snapshot reader blocks the auto-rebuild");

                var seen = 1;
                while (reader.Read()) seen++;
                seen.Should().Be(Rows, "the reader keeps its snapshot");
            }

            new LiteDatabase(writer, disposeOnClose: false).GetCollection("docs").Count().Should().Be(Rows);
            this.Rebuilt().Should().BeTrue("with the reader gone, the invalid state triggers the rebuild");
        }

        private string ReadersDirectory => Path.GetFullPath(this.Filename) + "-readers";

        private bool HasLease() => Directory.Exists(this.ReadersDirectory) &&
            Directory.GetFiles(this.ReadersDirectory, "*.lease").Length > 0;

        private static int[] Ids(IBsonDataReader reader)
        {
            var ids = new System.Collections.Generic.List<int>();
            while (reader.Read()) ids.Add(reader.Current["_id"].AsInt32);
            return ids.ToArray();
        }

        [Theory]
        [InlineData(100, false)]
        [InlineData(101, true)]
        public void A_result_at_the_value_budget_is_buffered_and_one_past_it_streams_from_the_same_snapshot(int count, bool leased)
        {
            using (var db = new LiteDatabase(new ConnectionString { Filename = this.Filename }))
                db.GetCollection("docs").InsertBulk(Enumerable.Range(1, count).Select(id => new BsonDocument { ["_id"] = id }));

            using var engine = this.Open();
            using (var reader = engine.Query("docs", new Query()))
            {
                this.HasLease().Should().Be(leased);
                Ids(reader).Should().Equal(Enumerable.Range(1, count));
            }
            engine.EngineOpens.Should().Be(0, "a pure read opens no writable operation engine");
            engine.SnapshotOpens.Should().Be(1, "the buffered prefix and the streamed rest come from one snapshot, executed once");
        }

        [Fact]
        public void A_result_crossing_the_byte_budget_streams_and_one_just_below_it_is_buffered()
        {
            var docs = Enumerable.Range(1, 99).Select(id => new BsonDocument { ["_id"] = id, ["pad"] = new string('b', 900) }).ToArray();
            var fitting = 0;
            for (var total = 0; fitting < docs.Length; fitting++)
            {
                total += docs[fitting].GetBytesCount(true);
                if (total > 64 * 1024) break;
            }
            fitting.Should().BeLessThan(docs.Length);

            using (var db = new LiteDatabase(new ConnectionString { Filename = this.Filename }))
            {
                db.GetCollection("below").InsertBulk(docs.Take(fitting));
                db.GetCollection("above").InsertBulk(docs.Take(fitting + 1));
            }

            using var engine = this.Open();
            using (var reader = engine.Query("below", new Query()))
            {
                this.HasLease().Should().BeFalse();
                Ids(reader).Should().Equal(Enumerable.Range(1, fitting));
            }
            using (var reader = engine.Query("above", new Query()))
            {
                this.HasLease().Should().BeTrue();
                Ids(reader).Should().Equal(Enumerable.Range(1, fitting + 1));
            }
        }

        [Fact]
        public void A_streamed_prefix_matches_a_direct_reader_before_and_after_the_first_read()
        {
            this.Seed();
            using var engine = this.Open();
            using var reader = engine.Query("docs", new Query());
            reader.HasValues.Should().BeTrue();
            reader.Current["_id"].AsInt32.Should().Be(1, "like BsonDataReader, the first value is current before Read");
            var ids = Ids(reader);
            ids.Should().Equal(Enumerable.Range(1, Rows));
            reader.Read().Should().BeFalse();
        }

        [Fact]
        public void A_legacy_file_read_first_is_migrated_by_the_writable_open()
        {
            using var file = LegacyIndexFixtures.Extract("stale", null);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file.Filename }))
            {
                // A read-only open refuses a file that awaits index migration; the read
                // then takes the writable path, which migrates before reading.
                new LiteDatabase(engine, disposeOnClose: false).GetCollection("rows").FindAll().Should().NotBeEmpty();
                engine.EngineOpens.Should().Be(1);
            }
            File.ReadAllBytes(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.INDEX_FILE_VERSION);
        }

        [Fact]
        public void A_lease_is_live_for_another_registry_while_held_and_leaves_no_file_behind()
        {
            var registry = new LiteDB.Client.Shared.SharedReaderRegistry(this.Filename);
            var other = new LiteDB.Client.Shared.SharedReaderRegistry(this.Filename);
            using (var lease = registry.Register(7))
            {
                // Another registry proves liveness by an exclusive open, which the held lease refuses.
                other.LiveVersions().Should().Equal(7);
                Directory.GetFiles(this.ReadersDirectory, "*.lease").Should().HaveCount(1);
            }
            Directory.GetFiles(this.ReadersDirectory, "*.lease").Should().BeEmpty(
                "a closed lease deletes itself instead of waiting to be proven dead");
            other.LiveVersions().Should().BeEmpty();
        }

        [Fact]
        public void A_first_read_creates_a_missing_database()
        {
            using var engine = this.Open();
            new LiteDatabase(engine, disposeOnClose: false).GetCollection("docs").Count().Should().Be(0);
            File.Exists(this.Filename).Should().BeTrue();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
