using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedOneFileDefaultAvailability_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Default_read_only_preserves_legacy_remnants(bool connectionString)
        {
            WithFile(filename =>
            {
                using (var db = Open(filename, false, false)) Seed(db);
                var paths = Directory.GetFiles(Path.GetDirectoryName(filename), "*", SearchOption.AllDirectories);
                var bytes = paths.Select(File.ReadAllBytes).ToArray();
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using (var db = Open(filename, true, null, connectionString)) Read(db);
                    Directory.GetFiles(Path.GetDirectoryName(filename), "*", SearchOption.AllDirectories).Should().BeEquivalentTo(paths);
                    for (var i = 0; i < paths.Length; i++) File.ReadAllBytes(paths[i]).Should().Equal(bytes[i]);
                }
                using (var db = Open(filename, false)) { Read(db); Seed(db); }
                using (var db = Open(filename, true)) Read(db);
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Default_read_only_and_writable_facades_coexist(bool readerFirst, bool connectionString)
        {
            WithFile(filename =>
            {
                using (var seed = Open(filename, false)) Seed(seed);
                using (var first = Open(filename, readerFirst, null, connectionString))
                using (var second = Open(filename, !readerFirst, null, connectionString))
                {
                    var reader = readerFirst ? first : second;
                    var writer = readerFirst ? second : first;
                    Read(reader);
                    writer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "later" });
                    reader.GetCollection("docs").FindOne(Query.EQ("value", "later"))["_id"].Should().Be(2);
                    Action write = () => reader.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 });
                    write.Should().Throw<Exception>();
                    writer.GetCollection("docs").Count().Should().Be(2);
                }
                using (var db = Open(filename, false)) db.GetCollection("docs").Count().Should().Be(2);
            });
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void Default_native_tmpfs_preserves_indexed_data_and_legacy_protocol()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)) return;
            var directory = Path.Combine("/dev/shm", "litedb-default-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var filename = Path.Combine(directory, "test.db");
                SharedCoordinationPolicy.VolumeFailure(filename).Should().NotBeNull();
                using (var db = Open(filename, false, false)) Seed(db);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using (var db = Open(filename, false)) { Read(db); Seed(db); }
                    using (var db = Open(filename, true)) Read(db);
                    Directory.Exists(filename + "-readers").Should().BeTrue();
                    File.Exists(filename + "-shm").Should().BeFalse();
                }
            }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
        }
#endif

        private static LiteDatabase Open(string filename, bool readOnly, bool? oneFile = null, bool connectionString = false)
        {
            var connection = new ConnectionString { Filename = filename, Connection = ConnectionType.Shared, ReadOnly = readOnly };
            var settings = new EngineSettings { Filename = filename, ReadOnly = readOnly };
            if (oneFile.HasValue) { connection.SharedOneFile = oneFile.Value; settings.SharedOneFile = oneFile.Value; }
            return connectionString ? new LiteDatabase(connection) : new LiteDatabase(new SharedEngine(settings));
        }
        private static void Seed(LiteDatabase db)
        {
            db.GetCollection("docs").Upsert(new BsonDocument { ["_id"] = 1, ["value"] = "accepted" });
            db.GetCollection("docs").EnsureIndex("value");
            Read(db);
        }
        private static void Read(LiteDatabase db) => db.GetCollection("docs").FindOne(Query.EQ("value", "accepted"))["_id"].Should().Be(1);
        private static void WithFile(Action<string> action)
        {
            var root = Path.GetTempPath();
#if NET8_0_OR_GREATER
            if (SharedMemoryAuthority.IsPlatformQualified) root = SharedMappedDirectory.Root;
#endif
            var directory = Path.Combine(root, "shared-availability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { action(Path.Combine(directory, "test.db")); }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
        }
    }
}
