using LiteDB;
using LiteDB.Engine;

using System.Reflection.Emit;
using System.Reflection.PortableExecutable;

var password = "46jLz5QWd5fI3m4LiL2r";
var path = $"C:\\LiteDB\\Examples\\CrashDB_{DateTime.Now.Ticks}.db";

var settings = new EngineSettings
{
    AutoRebuild = true,
    Filename = path,
    Password = password
};

var data = Enumerable.Range(1, 10_000).Select(i => new BsonDocument
{
    ["_id"] = i,
    ["name"] = Faker.Fullname(),
    ["age"] = Faker.Age(),
    ["created"] = Faker.Birthday(),
    ["lorem"] = Faker.Lorem(5, 25)
}).ToArray();

try
{
    using (var db = new LiteEngine(settings))
    {
#if DEBUG
        db.SimulateDiskWriteFail = (page) =>
        {
            var p = new BasePage(page);

            if (p.PageID == 248)
            {
                page.Write((uint)123123123, 8192-4);
            }
        };
#endif

        db.Pragma("USER_VERSION", 123);

        db.EnsureIndex("col1", "idx_age", "$.age", false);

        db.Insert("col1", data, BsonAutoId.Int32);
        db.Insert("col2", data, BsonAutoId.Int32);

        var col1 = db.Query("col1", Query.All()).ToList().Count;
        var col2 = db.Query("col2", Query.All()).ToList().Count;

        Console.WriteLine("Inserted Col1: " + col1);
        Console.WriteLine("Inserted Col2: " + col2);
    }
}
catch (Exception ex)
{
    Console.WriteLine("ERROR: " + ex.Message);
}

Console.WriteLine("Recovering database...");

using (var db = new LiteEngine(settings))
{
    var col1 = db.Query("col1", Query.All()).ToList().Count;
    var col2 = db.Query("col2", Query.All()).ToList().Count;

    Console.WriteLine($"Col1: {col1}");
    Console.WriteLine($"Col2: {col2}");

    var errors = new BsonArray(db.Query("_rebuild_errors", Query.All()).ToList()).ToString();

    Console.WriteLine("Errors: " + errors);

}

/*
var errors = new List<FileReaderError>();
var fr = new FileReaderV8(settings, errors);

fr.Open();
var pragmas = fr.GetPragmas();
var cols = fr.GetCollections().ToArray();
var indexes = fr.GetIndexes(cols[0]);

var docs1 = fr.GetDocuments("col1").ToArray();
var docs2 = fr.GetDocuments("col2").ToArray();


Console.WriteLine("Recovered Col1: " + docs1.Length);
Console.WriteLine("Recovered Col2: " + docs2.Length);

Console.WriteLine("# Errors: ");
errors.ForEach(x => Console.WriteLine($"PageID: {x.PageID}/{x.Origin}/#{x.Position}[{x.Collection}]: " + x.Message));
*/

Console.WriteLine("\n\nEnd.");
Console.ReadKey();