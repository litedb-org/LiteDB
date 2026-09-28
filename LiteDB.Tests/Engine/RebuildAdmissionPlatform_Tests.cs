using System;
using System.ComponentModel;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildAdmissionPlatform_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Direct_open_without_named_mutexes_preserves_reads_and_persisted_writes(string password, bool readOnly)
        {
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            var attempts = 0;
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = password, ReadOnly = readOnly,
                CreateRebuildMutex = name => { attempts++; throw new PlatformNotSupportedException(); }
            };
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("original");
                db.GetCollection("rows").FindById(2)["value"].AsString.Should().Be("wal");
                db.GetCollection("unrelated").FindById(1)["value"].AsString.Should().Be("preserved");
                if (!readOnly)
                {
                    if (retry == 0) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "new" });
                    else db.GetCollection("rows").FindById(3)["value"].AsString.Should().Be("new");
                }
            }
            attempts.Should().Be(2);
            if (readOnly)
            {
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
            }
        }

        [Theory]
        [InlineData("shared")]
        [InlineData("recovery")]
        [InlineData("upgrade")]
        public void Replacement_and_shared_opens_require_mutex_support_before_touching_files(string mode)
        {
            using var file = RebuildOwnership_Tests.Seed(null);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            var failure = new PlatformNotSupportedException("named mutex unavailable");
            var settings = new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = mode == "recovery", Upgrade = mode == "upgrade",
                CreateRebuildMutex = name => throw failure
            };
            if (mode == "shared") settings.SharedReaderVersions = () => Array.Empty<int>();
            for (var retry = 0; retry < 2; retry++)
            {
                Action open = () => new LiteEngine(settings).Dispose();
                open.Should().Throw<PlatformNotSupportedException>().Which.Should().BeSameAs(failure);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
            }
        }

        [Theory]
        [InlineData("permission")]
        [InlineData("io")]
        [InlineData("wrapped-permission")]
        public void Ordinary_open_does_not_hide_other_admission_failures(string kind)
        {
            using var file = RebuildOwnership_Tests.Seed(null);
            var data = File.ReadAllBytes(file.Filename);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            Exception failure = kind == "permission" ? new UnauthorizedAccessException() :
                kind == "io" ? (Exception)new IOException("mutex unavailable") :
                new PlatformNotSupportedException("access control unavailable", new Win32Exception(5));
            Action open = () => new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, CreateRebuildMutex = name => throw failure
            }).Dispose();
            open.Should().Throw<Exception>().Which.Should().BeSameAs(failure);
            File.ReadAllBytes(file.Filename).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
        }

        [Fact]
        public void Explicit_rebuild_refuses_without_closing_engine_or_leaking_transaction_lock()
        {
            using var file = RebuildOwnership_Tests.Seed(null);
            var settings = new EngineSettings
            {
                Filename = file.Filename, CreateRebuildMutex = name => throw new PlatformNotSupportedException()
            };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var data = File.ReadAllBytes(file.Filename);
                var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
                for (var retry = 0; retry < 2; retry++)
                {
                    Action rebuild = () => engine.Rebuild();
                    rebuild.Should().Throw<PlatformNotSupportedException>();
                    File.ReadAllBytes(file.Filename).Should().Equal(data);
                    File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
                    db.GetCollection("rows").Count().Should().Be(2);
                }
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after rejection" });
                File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
            }
            using var reopened = new LiteDatabase(new LiteEngine(settings));
            reopened.GetCollection("rows").FindById(3)["value"].AsString.Should().Be("after rejection");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unsupported_mutex_does_not_bypass_an_incomplete_rebuild_marker(bool readOnly)
        {
            using var file = RebuildOwnership_Tests.Seed(null);
            var marker = RebuildRecovery.GetMarkerFilename(file.Filename);
            File.WriteAllText(marker, "incomplete installation");
            try
            {
                var data = File.ReadAllBytes(file.Filename);
                var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
                Action open = () => new LiteEngine(new EngineSettings
                {
                    Filename = file.Filename, ReadOnly = readOnly,
                    CreateRebuildMutex = name => throw new PlatformNotSupportedException()
                }).Dispose();
                open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
                File.ReadAllText(marker).Should().Be("incomplete installation");
            }
            finally { File.Delete(marker); }
        }
    }
}
