using System;
using System.IO;
using System.Linq;
using LiteDB.Tests.Issues;

internal static class MonoProgram
{
    private static int Main()
    {
        var failures = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
            try
            {
                Issue1472_CompoundQueryScenario.Run(filename, Console.WriteLine);
                Console.WriteLine("ATTEMPT_" + attempt + ": all query and persistence assertions passed");
            }
            catch (AggregateException error) when (IsReportedFailure(error))
            {
                failures++;
                var matching = error.Flatten().InnerExceptions;
                Console.WriteLine("ATTEMPT_" + attempt + ": matchingFailures=" + matching.Count + "; " + matching[0]);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 20;
            }
            finally
            {
                foreach (var path in Directory.GetFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(filename) + "*"))
                {
                    File.Delete(path);
                }
            }
        }
        Console.WriteLine("REPEATS_1472: attempts=3, reportedFailures=" + failures);
        Console.WriteLine(failures > 0 ? "BUG_1472_CONFIRMED" : "VERIFIED_1472");
        return failures > 0 ? 0 : 10;
    }

    private static bool IsReportedFailure(AggregateException error)
    {
        var failures = error.Flatten().InnerExceptions;
        return failures.Count > 0 && failures.All(failure =>
            failure is InvalidOperationException &&
            failure.Message == "Collection was modified; enumeration operation may not execute." &&
            failure.StackTrace != null &&
            failure.StackTrace.Contains("LiteDB.BsonDocument.CopyTo") &&
            failure.StackTrace.Contains("LiteDB.Engine.IndexCost..ctor"));
    }
}
