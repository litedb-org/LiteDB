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
    [Fact]
    public async Task Published_result_waits_for_sharing_handle_and_preserves_payload()
    {
        var path = Path.GetTempFileName();
        using var retry = new ManualResetEventSlim();
        File.WriteAllText(path, "changed");
        var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var read = Task.Run(() => SnapshotFuzzer.ReadPublished(path, TimeSpan.FromSeconds(10), retry.Set));
        try
        {
            Assert.True(retry.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(read.IsCompleted);
            held.Dispose();
            var payload = await read;
            Assert.Equal("changed", payload);
            Assert.Throws<FuzzFailureException>(() => FuzzOracle.VerifySnapshotResult(null, payload));
            FuzzOracle.VerifySnapshotResult(null, "ok");
        }
        finally
        {
            held.Dispose();
            await read;
            File.Delete(path);
        }
    }

    [Fact]
    public void Published_result_persistent_lock_and_missing_file_fail()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => SnapshotFuzzer.ReadPublished(path, TimeSpan.Zero));
            File.Delete(path);
            Assert.Throws<FileNotFoundException>(() => SnapshotFuzzer.ReadPublished(path, TimeSpan.FromSeconds(10)));
        }
        finally { File.Delete(path); }
    }

}
