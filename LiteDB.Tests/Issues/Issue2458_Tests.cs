using System;
using System.IO;
using System.Threading.Tasks;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2458_Tests
{
    [Fact]
    public async Task NegativeSeekFails()
    {
        await using var db = DatabaseFactory.Create();
        var fs = db.FileStorage;
        await AddTestFileAsync("test", 1, fs);
        await using var stream = await fs.OpenReadAsync("test");
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }

    [Fact]
    public async Task SeekPastFileSucceds()
    {
        await using var db = DatabaseFactory.Create();
        var fs = db.FileStorage;
        await AddTestFileAsync("test", 1, fs);
        await using var stream = await fs.OpenReadAsync("test");
        stream.Position = int.MaxValue;
    }

    [Fact]
    public async Task SeekShortChunks()
    {
        await using var db = DatabaseFactory.Create();
        var fs = db.FileStorage;
        await using (var writeStream = await fs.OpenWriteAsync("test", "test"))
        {
            await writeStream.WriteAsync(new byte[] { 0 }, 0, 1);
            await writeStream.FlushAsync();
            await writeStream.WriteAsync(new byte[] { 1 }, 0, 1);
            await writeStream.FlushAsync();
            await writeStream.WriteAsync(new byte[] { 2 }, 0, 1);
        }

        await using var readStream = await fs.OpenReadAsync("test");
        readStream.Position = 2;
        Assert.Equal(2, readStream.ReadByte());
    }

    private static async Task AddTestFileAsync(string id, long length, ILiteStorage<string> fs)
    {
        await using var writeStream = await fs.OpenWriteAsync(id, id);
        var buffer = new byte[length];
        await writeStream.WriteAsync(buffer, 0, buffer.Length);
    }
}
