using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3124_LinqLiftedNulls;

/// <summary>
/// Repro of LiteDB issue #3124. Exit code 0 means the bug reproduced, which the known-bad
/// LiteDB pinned in the .csproj must do; exit code 1 means it did not, which the fixed
/// source must do. Catch the bug's own exception inside <see cref="Reproduce"/>: an escaping
/// exception exits 2 and is inconclusive.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = context.SharedDatabaseRoot
            ?? Path.Combine(Path.GetTempPath(), "Issue_3124_LinqLiftedNulls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var assembly = typeof(LiteDatabase).Assembly;
            Console.WriteLine($"LiteDB {assembly.FullName} loaded from {assembly.Location}");
            assembly.GetType("LiteDB.LiteDBPragmas")
                ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
            var reproduced = Reproduce(Path.Combine(directory, "repro.db"));
            host.SendResult(reproduced, reproduced ? "REPRODUCED: wrong query rows." : "FIXED: query rows match CLR.");
            return reproduced ? 0 : 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "The repro failed.", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    public class Row
    {
        public int Id { get; set; }
        public int? N { get; set; }
    }

    /// <summary>Returns true only when the wrong behavior of issue #3124 is observed.</summary>
    private static bool Reproduce(string databasePath)
    {
        using var db = new LiteDatabase(databasePath);
        var rows = db.GetCollection<Row>("rows");
        rows.DeleteAll();
        rows.Insert(new[] { new Row { Id = 1, N = null }, new Row { Id = 2, N = -1 },
            new Row { Id = 3, N = 5 }, new Row { Id = 4, N = 6 } });
        var stored = rows.FindAll().ToArray();
        var reproduced = false;
        var predicates = new Expression<Func<Row, bool>>[]
        {
            x => x.N < 5, x => x.N <= 5, x => 5 > x.N, x => 5 >= x.N,
            x => x.N > 5, x => x.N >= 5, x => x.N == null, x => x.N != null,
            x => x.N < 5 || x.N > 5, x => x.N != null && x.N <= 5
        };
        var knownBad = new[] { new[] { 1, 2 }, new[] { 1, 2, 3 }, new[] { 1, 2 }, new[] { 1, 2, 3 },
            new[] { 4 }, new[] { 3, 4 }, new[] { 1 }, new[] { 2, 3, 4 }, new[] { 1, 2, 4 }, new[] { 2, 3 } };
        for (var i = 0; i < predicates.Length; i++)
        {
            var predicate = predicates[i];
            var expected = stored.Where(predicate.Compile()).Select(x => x.Id).OrderBy(x => x).ToArray();
            var actual = rows.Find(predicate).Select(x => x.Id).OrderBy(x => x).ToArray();
            Console.WriteLine($"{predicate}: CLR [{string.Join(",", expected)}], LiteDB [{string.Join(",", actual)}]");
            if (!actual.SequenceEqual(expected) && !actual.SequenceEqual(knownBad[i]))
                throw new InvalidOperationException("Unexpected query outcome; not the lifted-null defect.");
            reproduced |= !expected.SequenceEqual(actual);
        }
        return reproduced;
    }
}
