using System;
using System.IO;
using LiteDB;
using LiteDB.Tests.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3082_AllocationPublication;

/// <summary>
/// Repro of LiteDB issue #3082. Exit code 0 means the bug reproduced, which the known-bad
/// LiteDB pinned in the .csproj must do; any other exit code means it did not, which the fixed
/// source must do. Catch the bug's own exception inside <see cref="Reproduce"/>: an escaping
/// exception counts as "did not reproduce".
/// </summary>
internal static class Program
{
    private static int Main()
    {
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")?
            .GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = context.SharedDatabaseRoot
            ?? Path.Combine(Path.GetTempPath(), "Issue_3082_AllocationPublication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var reproduced = Reproduce(Path.Combine(directory, "repro-" + context.InstanceIndex + ".db"));
            host.SendResult(reproduced, reproduced ? "The bug reproduced." : "The bug did not reproduce.");
            return reproduced ? 0 : 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "The repro failed.", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>Returns true only when the wrong behavior of issue #3082 is observed.</summary>
    private static bool Reproduce(string databasePath)
    {
        var reproduced = false;
        foreach (var password in new[] { (string)null, "secret" })
        foreach (var mode in new[] { "hole", "tail", "free" })
        {
            var path = databasePath + "-" + mode + (password == null ? "-plain" : "-encrypted");
            using (var db = AllocationWorkload.Open(path, password))
            {
                AllocationWorkload.Seed(db, mode);
                AllocationWorkload.Publish(db, mode);
            }
            using (var db = AllocationWorkload.Open(path, password))
            {
                AllocationWorkload.Verify(db);
                db.Checkpoint();
            }
            try { AllocationFileGraph.Verify(path, password); }
            catch (InvalidDataException error)
            {
                Console.WriteLine(mode + ": " + error.Message);
                reproduced = true;
            }
        }
        return reproduced;
    }
}
