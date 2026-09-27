using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using LiteDB;
using UnityEngine;

public sealed class LiteDbIl2CppSmoke : MonoBehaviour
{
    private IEnumerator Start()
    {
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        var exitCode = 0;

        try
        {
            Run();
            WriteResult("LITEDB_UNITY_IOS_IL2CPP_RESULT=PASS");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            WriteResult("LITEDB_UNITY_IOS_IL2CPP_RESULT=FAIL\n" + exception);
        }

        yield return new WaitForSecondsRealtime(1.0f);
        Environment.Exit(exitCode);
    }

    private static void Run()
    {
#if ENABLE_IL2CPP
        Debug.Log("Scripting backend: IL2CPP");
#else
        throw new InvalidOperationException("Player was not built with IL2CPP.");
#endif
        Debug.Log($"Unity: {Application.unityVersion}; platform: {Application.platform}");

        var runtimeFeature = typeof(object).Assembly
            .GetType("System.Runtime.CompilerServices.RuntimeFeature");
        var dynamicCodeSupported = runtimeFeature?
            .GetProperty("IsDynamicCodeSupported", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);
        Debug.Log($"RuntimeFeature.IsDynamicCodeSupported: {dynamicCodeSupported ?? "<missing>"}");

        var compiler = typeof(BsonExpression).Assembly.GetType("LiteDB.BsonExpressionCompiler");
        var singleArgumentDelegatesProperty = compiler?
            .GetProperty("UseSingleArgumentDelegates", BindingFlags.NonPublic | BindingFlags.Static);
#if LITEDB_FORCE_SINGLE_ARGUMENT_DELEGATES
        const bool forceSingleArgumentDelegates = true;
        singleArgumentDelegatesProperty?.SetValue(null, true);
#else
        const bool forceSingleArgumentDelegates = false;
#endif
        var singleArgumentDelegates = singleArgumentDelegatesProperty?.GetValue(null);
        Debug.Log($"Force single-argument expression delegates: {forceSingleArgumentDelegates}");
        Debug.Log($"LiteDB single-argument expression delegates: {singleArgumentDelegates ?? "<missing>"}");
        if (forceSingleArgumentDelegates)
        {
            Require(singleArgumentDelegates is bool enabled && enabled,
                "Could not enable LiteDB's no-dynamic-code expression path.");
        }

        var path = Path.Combine(Application.temporaryCachePath, "litedb-unity-ios-il2cpp.db");
        if (File.Exists(path)) File.Delete(path);

        try
        {
            using (var database = new LiteDatabase(path))
            {
                var collection = database.GetCollection("items");
                collection.Insert(new BsonDocument
                {
                    ["_id"] = 1,
                    ["name"] = "Ada",
                    ["score"] = 36,
                    ["items"] = new BsonArray { 3, 1, 2 }
                });
                collection.Insert(new BsonDocument
                {
                    ["_id"] = 2,
                    ["name"] = "Alan",
                    ["score"] = 41,
                    ["items"] = new BsonArray { 4, 5 }
                });

                Require(collection.EnsureIndex("score", "$.score"), "Index creation failed.");
                Require(collection.Query().Where("score BETWEEN @0 AND @1", 30, 40).Count() == 1,
                    "Parameterized indexed query failed.");

                var document = collection.FindById(1);
                Require(document != null && document["name"].AsString == "Ada", "Document round trip failed.");

                var expressions = new Dictionary<string, string>
                {
                    ["UPPER($.name)"] = "\"ADA\"",
                    ["ARRAY(MAP($.items[*] => @ * $.score))"] = "[108,36,72]",
                    ["ARRAY(FILTER($.items[*] => @ > 1))"] = "[3,2]",
                    ["{ name: $.name, adult: $.score > 18 }"] = "{\"name\":\"Ada\",\"adult\":true}",
                    ["$.items[*] ANY = 2"] = "true"
                };

                foreach (var pair in expressions)
                {
                    var value = BsonExpression.Create(pair.Key).Execute(document).Single();
                    Require(JsonSerializer.Serialize(value) == pair.Value,
                        $"Expression `{pair.Key}` returned {JsonSerializer.Serialize(value)}.");
                }

                using var reader = database.Execute(
                    "SELECT UPPER(name) AS label, score + 1 AS next FROM items WHERE score BETWEEN 30 AND 40");
                var row = reader.Single().AsDocument;
                Require(row["label"].AsString == "ADA" && row["next"].AsInt32 == 37,
                    "SQL expression execution failed.");
            }

            using var reopened = new LiteDatabase(path);
            Require(reopened.GetCollection("items").FindById(2)?["name"].AsString == "Alan",
                "Durable reopen failed.");
            Debug.Log("LiteDB document, index, expression, SQL, persistence, and reopen scenarios passed.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteResult(string result)
    {
        Debug.Log(result);
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "unity-result.txt"), result);
    }
}
