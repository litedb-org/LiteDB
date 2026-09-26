using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class CheckpointedFileVerifier_Tests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wal")]
    [InlineData("truncated")]
    public void Raw_validation_is_read_only_and_rejects_uncheckpointed_or_broken_files(string state)
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-raw-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filename = Path.Combine(root, "test.db");
            using (var db = new LiteDatabase(filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                for (var id = 0; id < 50; id++)
                    rows.Insert(new BsonDocument { ["_id"] = id, ["value"] = id * 7 });
            }
            var log = Path.Combine(root, "test-log.db");
            if (state == "wal") File.WriteAllBytes(log, new byte[] { 1, 2, 3 });
            if (state == "truncated")
            {
                using var file = File.OpenWrite(filename);
                file.SetLength(file.Length - 1);
            }
            var before = File.ReadAllBytes(filename);
            var options = FuzzOptions.Parse(new[] { "--database", filename, "--artifact-dir", Path.Combine(root, "oracle") });
            if (state == "valid") Assert.Equal(0, CheckpointedFileVerifier.Run(options));
            else if (state == "wal") Assert.Throws<InvalidOperationException>(() => CheckpointedFileVerifier.Run(options));
            else Assert.Throws<FuzzFailureException>(() => CheckpointedFileVerifier.Run(options));
            Assert.Equal(before, File.ReadAllBytes(filename));
            if (state == "wal") Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(log));
        }
        finally { Directory.Delete(root, true); }
    }
}
