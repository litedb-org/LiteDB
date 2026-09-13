using System;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2458_Tests
{
    [Fact]
    public void NegativeSeekFails()
    {
        using var db = new LiteDatabase(":memory:");
        var fs = db.FileStorage;
        AddTestFile("test", 1, fs);
        using Stream stream = fs.OpenRead("test");
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }

    //https://learn.microsoft.com/en-us/dotnet/api/system.io.stream.position?view=net-8.0 says seeking to a position
    //beyond the end of a stream is supported, so implementations should support it (error on read).
    [Fact]
    public void SeekPastFileSucceds()
    {
        using var db = new LiteDatabase(":memory:");
        var fs = db.FileStorage;
        AddTestFile("test", 1, fs);
        using Stream stream = fs.OpenRead("test");
        stream.Position = Int32.MaxValue;
    }

    [Fact]
    public void SeekShortChunks()
    {
        using var db = new LiteDatabase(":memory:");
        var fs = db.FileStorage;
        using(Stream writeStream = fs.OpenWrite("test", "test"))
        {
            writeStream.WriteByte(0);
            writeStream.Flush(); //Create single-byte chunk just containing a 0
            writeStream.WriteByte(1);
            writeStream.Flush();
            writeStream.WriteByte(2);
        }
        using Stream readStream = fs.OpenRead("test");
        readStream.Position = 2;
        Assert.Equal(2, readStream.ReadByte());
    }

    private void AddTestFile(string id, long length, ILiteStorage<string> fs)
    {
        using Stream writeStream = fs.OpenWrite(id, id);
        writeStream.Write(new byte[length]);
    }
}