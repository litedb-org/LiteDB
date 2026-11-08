using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedOneFileDefault_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Default_selects_qualified_protocol_and_preserves_indexed_data(bool connectionString)
        {
            Exercise(connectionString, legacy: false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_opt_out_round_trips_and_uses_legacy_sidecars(bool connectionString)
        {
            var original = new ConnectionString { Filename = "test.db", Connection = ConnectionType.Shared, SharedOneFile = false };
            new ConnectionString(original.ToString()).SharedOneFile.Should().BeFalse();
            Exercise(connectionString, legacy: true);
        }

        [Fact]
        public void First_writable_default_open_upgrades_legacy_sidecars_repeatably()
        {
            WithFile(filename =>
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = false }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false)) WriteAndRead(db);
                Directory.Exists(filename + "-readers").Should().BeTrue();
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var engine = new SharedEngine(new EngineSettings { Filename = filename });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    db.GetCollection("docs").FindOne(Query.EQ("value", "accepted"))["_id"].Should().Be(1);
                    // Also prove writable fallback after the cold indexed read. ABI-1
                    // creates its status pages on writer participation, not the first read.
                    WriteAndRead(db);
                    AssertFiles(filename, SharedProtocolDefaults.OneFile);
                }
            });
        }

        private static void Exercise(bool connectionString, bool legacy)
        {
            WithFile(filename =>
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var connection = new ConnectionString { Filename = filename, Connection = ConnectionType.Shared };
                    var settings = new EngineSettings { Filename = filename };
                    if (legacy) { connection.SharedOneFile = false; settings.SharedOneFile = false; }
                    using var engine = connectionString ? null : new SharedEngine(settings);
                    using var db = connectionString ? new LiteDatabase(connection) : new LiteDatabase(engine, disposeOnClose: false);
                    WriteAndRead(db);
                    AssertFiles(filename, !legacy && SharedProtocolDefaults.OneFile);
                }
            });
        }

        private static void WriteAndRead(LiteDatabase db)
        {
            var docs = db.GetCollection("docs");
            docs.Upsert(new BsonDocument { ["_id"] = 1, ["value"] = "accepted" });
            docs.EnsureIndex("value");
            docs.FindOne(Query.EQ("value", "accepted"))["_id"].Should().Be(1);
        }

        private static void AssertFiles(string filename, bool mapped)
        {
            Directory.Exists(filename + "-readers").Should().Be(!mapped);
            // Framework uses reader leases without the modern ABI-1 status pages.
#if NET8_0_OR_GREATER
            Directory.GetFiles(Path.GetDirectoryName(filename), "test.db-shared-*").Should().HaveCount(mapped ? 0 : 2);
#else
            Directory.GetFiles(Path.GetDirectoryName(filename), "test.db-shared-*").Should().BeEmpty();
#endif
            if (mapped) File.Exists(filename + "-shm").Should().BeTrue();
        }

        private static void WithFile(Action<string> action)
        {
            var root = Path.GetTempPath();
#if NET8_0_OR_GREATER
            if (SharedProtocolDefaults.OneFile) root = SharedMappedDirectory.Root;
#endif
            var directory = Path.Combine(root, "shared-default-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { action(Path.Combine(directory, "test.db")); }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
        }
    }
}
