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

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
