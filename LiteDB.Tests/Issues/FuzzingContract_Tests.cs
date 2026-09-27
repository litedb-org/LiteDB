using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class FuzzingContract_Tests
    {
        [Fact]
        public void Read_only_write_rejection_keeps_instance_usable()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
            Action write = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            write.Should().Throw<IOException>();
            db.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void Invalid_pragma_rejection_keeps_instance_usable()
        {
            using var db = new LiteDatabase(new MemoryStream());
            Action invalid = () => db.Timeout = TimeSpan.Zero;
            invalid.Should().Throw<LiteException>();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            db.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void File_replacement_never_splices_chunks_into_open_reader()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var oldBytes = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE * 2 + 17)
                .Select(index => (byte)index).ToArray();
            var newBytes = oldBytes.Select(value => (byte)(value ^ 0x5a)).ToArray();
            using (var source = new MemoryStream(oldBytes)) db.FileStorage.Upload("id", "old.bin", source);
            using var reader = db.FileStorage.OpenRead("id");
            var prefix = new byte[100];
            reader.Read(prefix, 0, prefix.Length).Should().Be(prefix.Length);
            using (var source = new MemoryStream(newBytes)) db.FileStorage.Upload("id", "new.bin", source);
            Action continueReading = () => reader.CopyTo(Stream.Null);
            continueReading.Should().Throw<LiteException>();
            using var output = new MemoryStream();
            db.FileStorage.Download("id", output);
            output.ToArray().Should().Equal(newBytes);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("[1,")]
        [InlineData("{x:'\\uZZZZ'}")]
        [InlineData("{x:1} trailing")]
        public void Malformed_json_is_rejected_cleanly(string json)
        {
            var error = Record.Exception(() => JsonSerializer.Deserialize(json));
            Assert.True(error is LiteException or FormatException, error?.ToString());
        }

        [Fact]
        public void Truncated_bson_scalar_reports_LiteException_instead_of_index_error()
        {
            var bytes = new byte[] { 8, 0, 0, 0, 0x08, 0 };
            using var reader = new BufferReader(bytes);
            Action read = () => reader.ReadDocument().GetValue();
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Bson_document_without_final_terminator_is_rejected()
        {
            var valid = BsonSerializer.Serialize(new BsonDocument { ["value"] = true });
            var truncated = valid.Take(valid.Length - 1).ToArray();
            Action read = () => BsonSerializer.Deserialize(truncated);
            read.Should().Throw<LiteException>();
        }

        [Fact]
        public void Mutated_bson_binary_length_is_rejected_before_allocation()
        {
            var bytes = new byte[]
            {
                0x1c, 0, 0, 0, 0x05, 0x08, 0x10, 0, 0, 0, 0, 0x04, 0xb6, 0x68,
                0x5c, 0x4b, 0x1a, 0x27, 0xeb, 0x1c, 0xb2, 0xe9, 0x76, 0xb8, 0x20, 0x2b, 0x73, 0x5e
            };
            Action read = () => BsonSerializer.Deserialize(bytes);
            read.Should().Throw<LiteException>();
        }

        [Theory]
        [InlineData("after-log-backup")]
        [InlineData("after-source-backup")]
        [InlineData("after-temp-install")]
        public void Interrupted_rebuild_leaves_a_complete_file(string phase)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.GetCollection("rows").InsertBulk(Enumerable.Range(1, 20)
                    .Select(id => new BsonDocument { ["_id"] = id, ["value"] = id }));
                seed.Checkpoint();
            }
            RebuildService.SimulateInstallFailure = current =>
            {
                if (current == phase) throw new IOException("injected");
            };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<IOException>();
            }
            finally { RebuildService.SimulateInstallFailure = null; }
            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").Count().Should().Be(20);
        }

        [Fact]
        public void Failure_after_password_rebuild_install_restores_the_original_data_and_wal_pair()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("injected");
            };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild(new RebuildOptions { Password = "new-password" });
                rebuild.Should().Throw<IOException>();
            }
            finally { RebuildService.SimulateInstallFailure = null; }

            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
        }

        [Fact]
        public async Task Rebuild_waits_for_an_active_writer_before_replacing_engine_services()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var exclusiveWaiting = new ManualResetEventSlim();
            engine.SimulateBeforeExclusiveAdmission = exclusiveWaiting.Set;

            engine.BeginTrans().Should().BeTrue();
            var rebuild = Task.Run(() => engine.Rebuild());
            exclusiveWaiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" } },
                BsonAutoId.Int32).Should().Be(1);
            engine.Commit().Should().BeTrue();

            await rebuild;
            var query = Query.All();
            query.Where.Add(Query.EQ("_id", 1));
            using var reader = engine.Query("rows", query);
            reader.Single()["value"].AsString.Should().Be("acknowledged");
        }
    }
}
