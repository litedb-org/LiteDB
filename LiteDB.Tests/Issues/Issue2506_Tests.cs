using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2506_Tests
{
    [Fact]
    public async Task Test()
    {
        await using LiteDatabase dataBase = new("demo.db");

        ILiteStorage<string> fileStorage = dataBase.GetStorage<string>("myFiles", "myChunks");

        using MemoryStream emptyStream = new();
        await fileStorage.UploadAsync("photos/2014/picture-01.jpg", "picture-01.jpg", emptyStream);

        LiteFileInfo<string> file = await fileStorage.FindByIdAsync("photos/2014/picture-01.jpg");
        Assert.NotNull(file);

        await file.SaveAsAsync(Path.Combine(Path.GetTempPath(), "new-picture.jpg"));

        List<LiteFileInfo<string>> files = new();
        await foreach (var info in fileStorage.FindAsync("_id LIKE 'photos/2014/%'"))
        {
            files.Add(info);
        }

        Assert.Single(files);

        List<LiteFileInfo<string>> files2 = new();
        await foreach (var info in fileStorage.FindAsync("_id LIKE @0", cancellationToken: default, "photos/2014/%"))
        {
            files2.Add(info);
        }

        Assert.Single(files2);
    }
}
