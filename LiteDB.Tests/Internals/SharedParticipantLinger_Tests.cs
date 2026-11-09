#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFileRegistry")]
    public class SharedParticipantLinger_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-linger-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedParticipantLinger_Tests() => Directory.CreateDirectory(_directory);
        private EngineSettings Settings(int milliseconds) => new EngineSettings
        {
            Filename = Filename, SharedOneFile = true, SharedLingerMilliseconds = milliseconds
        };

        [OneFileFact]
        public void Reopen_reuses_the_participant_without_a_second_bootstrap()
        {
            var joins = 0;
            SharedMemoryAuthority.BootstrapStage = stage => { if (stage == "claimed") joins++; };
            try
            {
                SharedMemoryReaderTable table;
                using (var first = new SharedEngine(Settings(1000))) table = first.MappedReaderTable;
                table.Closed.Should().BeFalse();
                using (var second = new SharedEngine(Settings(1000))) second.MappedReaderTable.Should().BeSameAs(table);
                joins.Should().Be(1);
                SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
                using var cold = new SharedEngine(Settings(0));
                cold.MappedReaderTable.Should().NotBeSameAs(table);
                joins.Should().Be(2);
            }
            finally { SharedMemoryAuthority.BootstrapStage = null; }
        }

        [OneFileFact]
        public void Expiry_releases_native_admission_and_preserves_committed_indexed_payloads()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(100)))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                table = engine.MappedReaderTable;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["key"] = "saved", ["payload"] = new string('x', 2048) });
                db.GetCollection("docs").EnsureIndex("key");
            }
            table.Closed.Should().BeFalse();
            SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(Filename);
                db.GetCollection("docs").FindOne(Query.EQ("key", "saved"))["payload"].AsString.Should().Be(new string('x', 2048));
                db.GetCollection("docs").Count().Should().Be(1);
            }
        }

        [OneFileFact]
        public void Zero_preserves_synchronous_final_close()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(0))) table = engine.MappedReaderTable;
            table.Closed.Should().BeTrue();
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Count().Should().Be(0);
        }

        public void Dispose()
        {
            // Prototype has no release API yet. Wait for the bounded test interval;
            // this is replaced by explicit release in the next implementation step.
            Thread.Sleep(1100);
            Directory.Delete(_directory, true);
        }
    }
}
#endif
