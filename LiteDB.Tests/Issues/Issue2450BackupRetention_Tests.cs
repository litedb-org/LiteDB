using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2450BackupRetention_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Repeated_encrypted_rebuild_replaces_only_canonical_backup(ConnectionType connection)
        {
            using var file = new TempFile();
            var backup = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            var older = FileHelper.GetSuffixFile(file.Filename, "-backup-1", false);
            File.WriteAllText(older, "keep old backup");
            try
            {
                using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret", Connection = connection }))
                {
                    for (var id = 1; id <= 3; id++)
                    {
                        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = id });
                        db.Rebuild();
                        using var saved = new LiteDatabase(new ConnectionString { Filename = backup, Password = "secret", ReadOnly = true });
                        saved.GetCollection("rows").Count().Should().Be(id);
                    }
                }
                File.ReadAllText(older).Should().Be("keep old backup");
            }
            finally { File.Delete(backup); File.Delete(older); }
        }

        [Fact]
        public void Failed_rebuild_installation_keeps_committed_original_readable()
        {
            using var file = new TempFile();
            var backup = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            Directory.CreateDirectory(backup);
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 42 });
                    Action rebuild = () => db.Rebuild();
                    rebuild.Should().Throw<Exception>();
                }
                using var reopened = new LiteDatabase(file.Filename);
                Assert.NotNull(reopened.GetCollection("rows").FindById(42));
                Directory.Exists(backup).Should().BeTrue();
            }
            finally
            {
                Directory.Delete(backup);
                var temporary = FileHelper.GetSuffixFile(file.Filename, "-temp", false);
                File.Delete(temporary);
                File.Delete(FileHelper.GetLogFile(temporary));
            }
        }
    }
}
