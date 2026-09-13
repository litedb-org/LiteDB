using System;
using System.IO;
using System.Linq;

using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildBackup_Tests
    {
        [Fact]
        public void Rebuild_keeps_data_and_log_backups_by_default()
        {
            using var file = new TempFile();
            var paths = new RebuildPaths(file.Filename);

            try
            {
                CreateDatabaseWithLog(file.Filename);
                var originalData = File.ReadAllBytes(paths.Data);
                var originalLog = File.ReadAllBytes(paths.Log);

                using (var db = new LiteDatabase(paths.Data))
                {
                    db.Rebuild(new RebuildOptions());
                }

                File.ReadAllBytes(paths.BackupData).Should().Equal(originalData);
                File.ReadAllBytes(paths.BackupLog).Should().Equal(originalLog);
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Rebuild_without_backup_deletes_data_and_log_backups_after_success()
        {
            using var file = new TempFile();
            var paths = new RebuildPaths(file.Filename);

            try
            {
                CreateDatabaseWithLog(file.Filename);

                using (var db = new LiteDatabase(paths.Data))
                {
                    db.Rebuild(new RebuildOptions { CreateBackup = false });
                }

                File.Exists(paths.BackupData).Should().BeFalse();
                File.Exists(paths.BackupLog).Should().BeFalse();
                using var reopened = new LiteDatabase(paths.Data);
                reopened.GetCollection("items").Count().Should().Be(1);
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Upgrade_without_backup_deletes_original_after_success()
        {
            using var file = new TempFile("../../../Resources/v4.db");
            var paths = new RebuildPaths(file.Filename);

            try
            {
                using (var engine = new LiteEngine(new EngineSettings
                {
                    Filename = paths.Data,
                    Upgrade = true,
                    CreateBackupOnUpgrade = false
                }))
                {
                    engine.Query("col1", Query.All()).ToList().Count.Should().Be(3);
                }

                File.Exists(paths.BackupData).Should().BeFalse();
                File.Exists(paths.BackupLog).Should().BeFalse();
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Failed_upgrade_without_backup_leaves_original_unchanged()
        {
            using var file = new TempFile("../../../Resources/Issue_2494_EncryptedV4.db");
            var paths = new RebuildPaths(file.Filename);
            var original = File.ReadAllBytes(paths.Data);

            try
            {
                Action upgrade = () => new LiteEngine(new EngineSettings
                {
                    Filename = paths.Data,
                    Password = "wrong-password",
                    Upgrade = true,
                    CreateBackupOnUpgrade = false
                });

                upgrade.Should().Throw<LiteException>();
                File.ReadAllBytes(paths.Data).Should().Equal(original);
                File.Exists(paths.BackupData).Should().BeFalse();

                using var recovered = new LiteEngine(new EngineSettings
                {
                    Filename = paths.Data,
                    Password = "pass123",
                    Upgrade = true
                });
                recovered.Query("PlayerDto", Query.All()).ToList().Should().NotBeEmpty();
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Failed_replacement_without_backup_keeps_recoverable_original()
        {
            using var file = new TempFile();
            var paths = new RebuildPaths(file.Filename);

            try
            {
                CreateDatabaseWithLog(paths.Data);
                var originalData = File.ReadAllBytes(paths.Data);
                var originalLog = File.ReadAllBytes(paths.Log);
                var rebuilder = new RebuildService(new EngineSettings { Filename = paths.Data })
                {
                    SimulateReplaceFail = () => throw new IOException("Injected replacement failure")
                };

                Action rebuild = () => rebuilder.Rebuild(new RebuildOptions { CreateBackup = false });

                rebuild.Should().Throw<IOException>().WithMessage("Injected replacement failure");
                File.ReadAllBytes(paths.BackupData).Should().Equal(originalData);
                File.ReadAllBytes(paths.BackupLog).Should().Equal(originalLog);

                File.Move(paths.BackupData, paths.Data);
                File.Move(paths.BackupLog, paths.Log);
                using var recovered = new LiteDatabase(paths.Data);
                recovered.GetCollection("items").Count().Should().Be(1);
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Failed_post_replacement_validation_without_backup_keeps_original()
        {
            using var file = new TempFile();
            var paths = new RebuildPaths(file.Filename);
            var originalCollation = new Collation("en-US/IgnoreCase");

            try
            {
                using (var created = new LiteEngine(new EngineSettings
                {
                    Filename = paths.Data,
                    Collation = originalCollation
                }))
                {
                    created.Insert("items", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
                    created.Checkpoint();
                }

                var original = File.ReadAllBytes(paths.Data);
                using var engine = new LiteEngine(new EngineSettings
                {
                    Filename = paths.Data,
                    Collation = originalCollation
                });
                Action rebuild = () => engine.Rebuild(new RebuildOptions
                {
                    Collation = new Collation("en-US/None"),
                    CreateBackup = false
                });

                rebuild.Should().Throw<LiteException>().WithMessage("*collation*");
                File.ReadAllBytes(paths.BackupData).Should().Equal(original);
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        [Fact]
        public void Partial_rebuild_without_backup_retains_original_files()
        {
            using var file = new TempFile();
            var paths = new RebuildPaths(file.Filename);

            try
            {
                CreateDatabaseWithLog(paths.Data);
                var originalData = File.ReadAllBytes(paths.Data);
                var originalLog = File.ReadAllBytes(paths.Log);
                var options = new RebuildOptions { CreateBackup = false };
                options.Errors.Add(new FileReaderError
                {
                    Exception = new InvalidDataException("Injected read error"),
                    Message = "Injected read error",
                    PageID = 1
                });

                var result = new RebuildService(new EngineSettings { Filename = paths.Data }).Rebuild(options);
                result.Complete();

                File.ReadAllBytes(paths.BackupData).Should().Equal(originalData);
                File.ReadAllBytes(paths.BackupLog).Should().Equal(originalLog);
                using var db = new LiteDatabase(paths.Data);
                db.GetCollection("_rebuild_errors").Count().Should().Be(1);
            }
            finally
            {
                paths.DeleteArtifacts();
            }
        }

        private static void CreateDatabaseWithLog(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.CheckpointSize = 0;
            db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
        }

        private sealed class RebuildPaths
        {
            public RebuildPaths(string data)
            {
                Data = data;
                Log = FileHelper.GetLogFile(data);
                BackupData = FileHelper.GetSuffixFile(data, "-backup", false);
                BackupLog = FileHelper.GetSuffixFile(Log, "-backup", false);
                TempData = FileHelper.GetSuffixFile(data, "-temp", false);
            }

            public string Data { get; }
            public string Log { get; }
            public string BackupData { get; }
            public string BackupLog { get; }
            public string TempData { get; }

            public void DeleteArtifacts()
            {
                File.Delete(Log);
                File.Delete(BackupData);
                File.Delete(BackupLog);
                File.Delete(TempData);
            }
        }
    }
}
