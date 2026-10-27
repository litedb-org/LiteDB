using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3124_LinqSignedZero;

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
            ?? Path.Combine(Path.GetTempPath(), "Issue_3124_LinqSignedZero-" + Guid.NewGuid().ToString("N"));
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
        public double D { get; set; }
        public float F { get; set; }
    }

    /// <summary>Returns true only when the wrong behavior of issue #3124 is observed.</summary>
    private static bool Reproduce(string databasePath)
    {
        using var db = new LiteDatabase(databasePath);
        var rows = db.GetCollection<Row>("rows");
        rows.DeleteAll();
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        rows.Insert(new[] { new Row { Id = 1, D = 0d, F = 0f },
            new Row { Id = 2, D = negativeZero, F = (float)negativeZero },
            new Row { Id = 3, D = 1d, F = 1f } });
        var stored = rows.FindAll().ToArray();
        var reproduced = false;
        // The double-negation case passed in 5.0.21 (two ignored minuses), but
        // regressed on dev when negation became subtraction from positive zero.
        var predicates = new Expression<Func<Row, bool>>[]
        {
            x => 1d / -(-x.D) < 0d, x => 1f / -(-x.F) < 0f,
            x => 1d / -x.D > 0d, x => 1f / -x.F > 0f
        };
        for (var i = 0; i < predicates.Length; i++)
        {
            var predicate = predicates[i];
            var expected = stored.Where(predicate.Compile()).Select(x => x.Id).OrderBy(x => x).ToArray();
            var actual = rows.Find(predicate).Select(x => x.Id).OrderBy(x => x).ToArray();
            Console.WriteLine($"{predicate}: CLR [{string.Join(",", expected)}], LiteDB [{string.Join(",", actual)}]");
            var knownBad = i < 2 ? Array.Empty<int>() : new[] { 1, 2 };
            if (!expected.SequenceEqual(new[] { 2 }) ||
                (!actual.SequenceEqual(expected) && !actual.SequenceEqual(knownBad)))
                throw new InvalidOperationException("Unexpected query outcome; not the signed-zero defect.");
            reproduced |= !expected.SequenceEqual(actual);
        }
        return reproduced;
    }
}
