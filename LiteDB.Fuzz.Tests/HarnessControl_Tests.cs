using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class HarnessControl_Tests
{
    [Fact]
    public void Legal_names_reuse_bounded_header_space_through_400_steps()
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var context = new FuzzContext("api-boundary", 529470, 400, null, root);
            using var db = new LiteDatabase(new MemoryStream());
            while (context.Next()) ApiBoundaryFuzzer.Names(context, db);
            Assert.Equal(400, context.Steps);
            Assert.Equal(21, db.GetCollectionNames().Count());
            Assert.Equal(40, db.GetCollection("under_score_0").Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(".read")]
    [InlineData(".result")]
    [InlineData(".stop")]
    public async Task Control_file_is_invisible_until_payload_is_closed(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var written = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        var path = Path.Combine(root, "reader" + extension);
        var payload = new string('x', 1024 * 1024);
        var writer = Task.Run(() => SnapshotFuzzer.Publish(path, payload, () =>
        {
            written.Set();
            Assert.True(publish.Wait(TimeSpan.FromSeconds(10)));
        }));
        try
        {
            Assert.True(written.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(path));
            // Force the consumer to inspect the pending publication. The producer
            // must have closed its handle even while paused before the rename.
            using (var pending = new FileStream(path + ".publishing", FileMode.Open,
                FileAccess.Read, FileShare.None))
                Assert.Equal(payload.Length, pending.Length);
            publish.Set();
            await writer;
            Assert.Equal(payload, File.ReadAllText(path));
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            publish.Set();
            await writer;
            Directory.Delete(root, true);
        }
    }
}
