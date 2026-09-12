using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2450_Tests
    {
        [Fact]
        public void Repeated_rebuild_keeps_one_current_readable_backup_and_preserves_unrelated_files()
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2450-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            var unrelated = Path.Combine(directory, "other-backup.db");
            File.WriteAllText(unrelated, "unrelated backup");
            try
            {
                using (var db = new LiteDatabase(file))
                {
                    for (var pass = 1; pass <= 3; pass++)
                    {
                        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = pass, ["value"] = pass * 11 });
                        db.Checkpoint();
                        db.Rebuild();
                        var backups = Directory.GetFiles(directory, "data-backup*.db");
                        backups.Should().ContainSingle("old rebuild backups must not accumulate");
                        using var backup = new LiteDatabase(new ConnectionString { Filename = backups.Single(), ReadOnly = true });
                        backup.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).OrderBy(x => x)
                            .Should().Equal(Enumerable.Range(1, pass).Select(i => i * 11));
                    }
                }
                using (var db = new LiteDatabase(file)) db.GetCollection("rows").Count().Should().Be(3);
                File.ReadAllText(unrelated).Should().Be("unrelated backup");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
