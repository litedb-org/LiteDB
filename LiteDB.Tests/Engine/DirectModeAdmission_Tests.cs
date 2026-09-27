using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DirectModeAdmission_Tests
    {
        [Fact]
        public void Connection_string_shared_diagnostics_do_not_open_storage()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared }))
            {
                db.GetSharedDiagnostics().Should().NotBeNull();
                db.GetSharedDiagnostics().CoordinatedReadHits.Should().Be(0);
                File.Exists(file.Filename).Should().BeFalse();
                File.Exists(file.Filename + "-shared-mode").Should().BeFalse();
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            using var direct = new LiteDatabase(file.Filename);
            direct.GetSharedDiagnostics().Should().BeNull();
            direct.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void Missing_directory_retains_ordinary_path_exception()
        {
            using var file = new TempFile();
            Action open = () => { using var engine = new LiteEngine(Path.Combine(file.Filename, "missing.db")); };
            open.Should().Throw<DirectoryNotFoundException>();
            Directory.Exists(file.Filename).Should().BeFalse();
        }

        [Fact]
        public void Existing_read_only_guard_and_participation_files_still_exclude_conflicting_connections()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename)) seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var mode = file.Filename + "-shared-mode";
            var live = SharedCoordinationFallback.LivePath(file.Filename);
            File.WriteAllBytes(live, SharedCoordinationProtocol.CreateParticipation(file.Filename));
            File.SetAttributes(mode, FileAttributes.ReadOnly);
            File.SetAttributes(live, FileAttributes.ReadOnly);
            try
            {
                using var direct = new LiteDatabase(file.Filename);
                direct.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                Action secondDirect = () => { using var engine = new LiteEngine(file.Filename); };
                secondDirect.Should().Throw<IOException>();
                using var shared = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
                Action write = () => shared.GetCollection("rows").DeleteAll();
                write.Should().Throw<IOException>();
            }
            finally
            {
                File.SetAttributes(mode, FileAttributes.Normal);
                File.SetAttributes(live, FileAttributes.Normal);
                File.Delete(live);
            }
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").Count().Should().Be(2);
        }

        [Fact]
        public void Orphan_authority_error_names_file_and_offline_recovery_preserves_data()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename)) seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file.Filename);
            var orphan = SharedCoordinationFallback.PagePath(file.Filename);
            File.WriteAllBytes(orphan, new byte[] { 17, 29, 37 });
            try
            {
                Action open = () => { using var engine = new LiteEngine(file.Filename); };
                var error = open.Should().Throw<IOException>().Which;
                error.Message.Should().Contain(orphan).And.Contain("Stop all connections");
                File.ReadAllBytes(orphan).Should().Equal(17, 29, 37);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
            }
            finally { File.Delete(orphan); }
            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").Count().Should().Be(1);
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void Read_only_connection_works_in_non_writable_directory_without_creating_guard()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Environment.UserName == "root") return;
            var directory = Path.Combine(Path.GetTempPath(), "litedb-mode-permissions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            var before = File.GetUnixFileMode(directory);
            try
            {
                using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                File.Delete(file + "-shared-mode");
                var data = File.ReadAllBytes(file);
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Action writable = () => { using var engine = new LiteEngine(file); };
                writable.Should().Throw<UnauthorizedAccessException>();
                using (var read = new LiteDatabase(new ConnectionString { Filename = file, ReadOnly = true }))
                    read.GetCollection("rows").Count().Should().Be(1);
                File.ReadAllBytes(file).Should().Equal(data);
                Directory.GetFiles(directory).Should().Equal(file);
            }
            finally
            {
                File.SetUnixFileMode(directory, before);
                Directory.Delete(directory, true);
            }
        }
#endif
    }
}
