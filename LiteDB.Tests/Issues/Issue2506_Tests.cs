using System.Collections.Generic;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2506_Tests
{
    [Fact]
    public void Test()
    {
        // Open database connection
        using LiteDatabase dataBase = new("demo.db");

        // Get the file metadata/chunks storage
        ILiteStorage<string> fileStorage = dataBase.GetStorage<string>("myFiles", "myChunks");

        // Upload empty test file to file storage
        using MemoryStream emptyStream = new();
        fileStorage.Upload("photos/2014/picture-01.jpg", "picture-01.jpg", emptyStream);

        // Find file reference by its ID (returns null if not found)
        LiteFileInfo<string> file = fileStorage.FindById("photos/2014/picture-01.jpg");
        Assert.NotNull(file);

        // Load and save file bytes to hard drive
        file.SaveAs(Path.Combine(Path.GetTempPath(), "new-picture.jpg"));

        // Find all files matching pattern
        IEnumerable<LiteFileInfo<string>> files = fileStorage.Find("_id LIKE 'photos/2014/%'");
        Assert.Single(files);
        // Find all files matching pattern using parameters
        IEnumerable<LiteFileInfo<string>> files2 = fileStorage.Find("_id LIKE @0", "photos/2014/%");
        Assert.Single(files2);
    }
}