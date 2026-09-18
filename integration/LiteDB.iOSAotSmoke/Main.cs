using System.Runtime.CompilerServices;
using Foundation;
using LiteDB.AotSmokeTests;
using LiteDB.Generated;
using UIKit;

namespace LiteDB.iOSAotSmoke;

public static class Application
{
    public static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

[Register(nameof(AppDelegate))]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        var label = new UILabel(Window.Bounds)
        {
            Lines = 0,
            TextAlignment = UITextAlignment.Center,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
        };
        Window.RootViewController = new UIViewController { View = label };
        Window.MakeKeyAndVisible();

        var exitCode = 0;
        try
        {
            SmokeRunner.Run();
            label.Text = "PASS: LiteDB iOS AOT smoke test";
            Log("LITEDB_IOS_AOT_RESULT=PASS");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            label.Text = "FAIL: " + exception.Message;
            Log("LITEDB_IOS_AOT_RESULT=FAIL\n" + exception);
        }

        _ = ExitAfterLogFlush(exitCode);
        return true;
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }

    private static async Task ExitAfterLogFlush(int exitCode)
    {
        await Task.Delay(750);
        Environment.Exit(exitCode);
    }
}

internal static class SmokeRunner
{
    internal static void Run()
    {
        Console.WriteLine("LiteDB iOS AOT smoke test");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");
        Console.WriteLine($"Dynamic code compiled: {RuntimeFeature.IsDynamicCodeCompiled}");

        RunDocumentScenario();
        RunGeneratedMappingScenarios();
        EngineScenarios.Run();
        ExpressionSweepScenarios.Run();
        SqlSweepScenarios.Run();
    }

    private static void RunDocumentScenario()
    {
        var path = Path.Combine(Path.GetTempPath(), $"litedb-ios-aot-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = new LiteDatabase(path))
            {
                var collection = database.GetCollection("items");
                collection.Insert(new BsonDocument
                {
                    ["_id"] = 1,
                    ["name"] = "native-aot",
                    ["score"] = 2,
                    ["values"] = new BsonArray { 1, 2, 3 }
                });
                collection.Insert(new BsonDocument
                {
                    ["_id"] = 2,
                    ["name"] = "secondary",
                    ["score"] = 4,
                    ["values"] = new BsonArray { 4, 5, 6 }
                });

                SmokeAssert.Require(collection.EnsureIndex("score", "$.score"), "Index creation failed.");
                SmokeAssert.Require(collection.Query().Where("score BETWEEN 1 AND 3").Count() == 1,
                    "Indexed expression query failed.");
                SmokeAssert.Require(BsonExpression.Create("ARRAY(MAP($.values[*] => @ * 2))")
                    .Execute(collection.FindById(1)!).Single().AsArray.SequenceEqual(new BsonArray { 2, 4, 6 }),
                    "Nested interpreted expression failed.");
                using var reader = database.Execute("SELECT UPPER(name) AS label FROM items WHERE score = 2");
                SmokeAssert.Require(reader.Single()["label"].AsString == "NATIVE-AOT", "SQL expression failed.");
            }

            using var reopened = new LiteDatabase(path);
            SmokeAssert.Require(reopened.GetCollection("items").FindById(1)?["name"].AsString == "native-aot",
                "Durable reopen failed.");
            Console.WriteLine("[PASS] Document, expression, SQL, index, and durable-reopen scenario");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RunGeneratedMappingScenarios()
    {
        var mapper = new FailOnGenericConversionMapper { SerializeNullValues = true };
        LiteDbGeneratedMappings.Register(mapper);

        using var database = new LiteDatabase(new MemoryStream(), mapper);
        var simple = database.GetGeneratedCollection<AotSimpleRecord>("simple");
        simple.Insert(new AotSimpleRecord { Name = "generated", Score = 7 });
        var read = simple.Query().Where(record => record.Score == 7).Single();
        SmokeAssert.Require(read.Name == "generated" && read.Id == 1,
            "Source-generated typed mapping failed.");

        GeneratedScalarWriteScenarios.Run(database);
        GeneratedValueScenarios.Run(database);
        GeneratedLinqScenarios.Run(database);
        Console.WriteLine("[PASS] Source-generated mapping scenarios");
    }
}
