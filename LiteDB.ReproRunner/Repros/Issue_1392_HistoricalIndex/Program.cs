using System.Reflection;

using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;
using LiteDB.Tests.Issues;

internal static class Program
{
    private static async Task<int> Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var source = bool.Parse(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "LiteDB.ReproRunner.UseProjectReference").Value);
        Console.WriteLine($"CACHE_RECYCLING_REQUIRED={source}; historical releases use their original cache policy");
        var failures = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await new Issue1392_Tests().VerifyConcurrentIndexReadsAndUpserts(source);
                Console.WriteLine($"ATTEMPT_{attempt}: all index, upsert and persisted ledger assertions passed");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (error.ToString().Contains("System.NotImplementedException") && error.ToString().Contains("ReadIndexKey"))
                    failures++;
                else return 20;
            }
        }
        Console.WriteLine($"REPEATS_1392: attempts=3, reportedFailures={failures}");
        Console.WriteLine(failures == 0 ? "VERIFIED_1392: no failure in these attempts" : "BUG_1392_CONFIRMED");
        return failures == 0 ? 10 : 0;
    }
}
