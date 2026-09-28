using System.Collections.Generic;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2506_Tests
{
    [Fact]
    public void Test()
    {
        using LiteDatabase dataBase = new(":memory:");

        // Get the file metadata/chunks storage
        ILiteStorage<string> fileStorage = dataBase.GetStorage<string>("myFiles", "myChunks");

        // Upload empty test file to file storage
        using MemoryStream emptyStream = new();
        fileStorage.Upload("photos/2014/picture-01.jpg", "picture-01.jpg", emptyStream);

        // Find file reference by its ID (returns null if not found)
        LiteFileInfo<string> file = fileStorage.FindById("photos/2014/picture-01.jpg");
        Assert.NotNull(file);

        // Exercise the filename-based SaveAs API on the configured test filesystem
        using var output = new TempFile();
        file.SaveAs(output.Filename);
        Assert.Equal(0, new FileInfo(output.Filename).Length);

        // Find all files matching pattern
        IEnumerable<LiteFileInfo<string>> files = fileStorage.Find("_id LIKE 'photos/2014/%'");
        Assert.Single(files);
        // Find all files matching pattern using parameters
        IEnumerable<LiteFileInfo<string>> files2 = fileStorage.Find("_id LIKE @0", "photos/2014/%");
        Assert.Single(files2);
    }
}