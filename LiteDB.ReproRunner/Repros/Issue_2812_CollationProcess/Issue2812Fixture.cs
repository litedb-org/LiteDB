using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;

internal static class Issue2812Fixture
{
    private const string CollationName = "de-DE/None";

    private static readonly string[] Keys = Enumerable.Range(0, 30)
        .SelectMany(i => new[] { "_", "-", ".", "a", "z", "ä", "é", "W", "v", "$" }
            .Select(prefix => prefix + i))
        .ToArray();

    internal static void Write(string path, string fingerprint)
    {
        using (var database = new LiteDatabase(new ConnectionString
        {
            Filename = path,
            Collation = new Collation(CollationName)
        }))
        {
            var primary = database.GetCollection("primary_rows");
            var secondary = database.GetCollection("secondary_rows");
            var metadata = database.GetCollection("metadata");

            var insertedPrimary = primary.Insert(Keys.Select((key, index) => CreatePrimaryRow(key, index, false)));
            var insertedSecondary = secondary.Insert(Keys.Select((key, index) => new BsonDocument
            {
                ["_id"] = index + 1,
                ["key"] = key,
                ["ordinal"] = index,
                ["payload"] = Payload(index, false)
            }));

            if (insertedPrimary != Keys.Length || insertedSecondary != Keys.Length)
            {
                throw new InvalidOperationException("fixture insert count mismatch");
            }

            if (!secondary.EnsureIndex("key", true))
            {
                throw new InvalidOperationException("secondary string index was not created");
            }

            metadata.Insert(new BsonDocument { ["_id"] = 1, ["sortOrder"] = fingerprint });
            database.Checkpoint();
        }

        File.WriteAllText(path + ".sort-order", fingerprint);
    }

    internal static int Read(string path, bool crossEnvironment, string writerFingerprint)
    {
        var before = File.ReadAllBytes(path);
        var database = Open(path, true, crossEnvironment, before);
        if (database == null)
        {
            return 10;
        }

        var primaryMisses = 0;
        var secondaryMisses = 0;

        using (database)
        {
            var primary = database.GetCollection("primary_rows");
            var secondary = database.GetCollection("secondary_rows");

            VerifyTraversal(primary.FindAll(), "_id", false);
            VerifyTraversal(secondary.FindAll(), "key", false);
            VerifyCounts(primary, secondary);

            var storedFingerprint = database.GetCollection("metadata").FindById(1)?["sortOrder"].AsString;
            if (!string.Equals(storedFingerprint, writerFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("database metadata disagrees with the sidecar sort-order ledger");
            }

            for (var index = 0; index < Keys.Length; index++)
            {
                var primaryRow = primary.FindById(Keys[index]);
                if (primaryRow == null) primaryMisses++;
                else VerifyRow(primaryRow, Keys[index], index, "_id", false);

                var secondaryRow = secondary.FindOne(Query.EQ("key", Keys[index]));
                if (secondaryRow == null) secondaryMisses++;
                else VerifyRow(secondaryRow, Keys[index], index, "key", false);
            }
        }

        AssertUnchanged(path, before);
        Console.WriteLine($"PROBE_2812_READ: cross={crossEnvironment}, primaryMisses={primaryMisses}/{Keys.Length}, secondaryMisses={secondaryMisses}/{Keys.Length}");
        return primaryMisses == 0 && secondaryMisses == 0 ? 10 : 0;
    }

    internal static int Upsert(string path, bool crossEnvironment)
    {
        var before = File.ReadAllBytes(path);
        var database = Open(path, false, crossEnvironment, before);
        if (database == null)
        {
            return 10;
        }

        try
        {
            using (database)
            {
                var primary = database.GetCollection("primary_rows");
                foreach (var pair in Keys.Select((key, index) => (key, index)))
                {
                    primary.Upsert(CreatePrimaryRow(pair.key, pair.index, true));
                }
            }

            using var reopened = new LiteDatabase(new ConnectionString { Filename = path, ReadOnly = true });
            var persisted = reopened.GetCollection("primary_rows").FindAll().ToList();
            var exact = HasExactLedgerForField(persisted, "_id", true);
            Console.WriteLine($"PROBE_2812_UPSERT: cross={crossEnvironment}, persistedRows={persisted.Count}/{Keys.Length}, exactLedger={exact}");
            return exact ? 10 : 0;
        }
        catch (Exception failure) when (crossEnvironment)
        {
            Console.WriteLine("PROBE_2812_UPSERT: cross-environment update failed without safe open rejection: " + failure.Message);
            return 0;
        }
    }

    internal static string GetRuntimeSortFingerprint()
    {
        var comparer = CultureInfo.GetCultureInfo("de-DE").CompareInfo;
        var ordered = Keys.OrderBy(key => key, Comparer<string>.Create(
            (left, right) => comparer.Compare(left, right, CompareOptions.None)));
        return string.Join("\u001f", ordered);
    }

    private static LiteDatabase? Open(string path, bool readOnly, bool crossEnvironment, byte[] before)
    {
        try
        {
            return new LiteDatabase(new ConnectionString { Filename = path, ReadOnly = readOnly });
        }
        catch (LiteException rejection) when (crossEnvironment && IsActionableRejection(rejection))
        {
            AssertUnchanged(path, before);
            Console.WriteLine("SAFE_REJECTION_2812: " + rejection.Message);
            return null;
        }
    }

    private static void VerifyTraversal(IEnumerable<BsonDocument> rows, string keyField, bool updated)
    {
        if (!HasExactLedgerForField(rows.ToList(), keyField, updated))
        {
            throw new InvalidOperationException($"{keyField} traversal disagrees with the immutable fixture ledger");
        }
    }

    private static bool HasExactLedgerForField(IReadOnlyCollection<BsonDocument> rows, string keyField, bool updated)
    {
        if (rows.Count != Keys.Length) return false;
        var groups = rows.GroupBy(row => row[keyField].AsString, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        for (var index = 0; index < Keys.Length; index++)
        {
            if (!groups.TryGetValue(Keys[index], out var matches) || matches.Length != 1 ||
                !IsExpectedRow(matches[0], Keys[index], index, keyField, updated)) return false;
        }

        return true;
    }

    private static void VerifyCounts(ILiteCollection<BsonDocument> primary, ILiteCollection<BsonDocument> secondary)
    {
        if (primary.Count() != Keys.Length || secondary.Count() != Keys.Length)
        {
            throw new InvalidOperationException("collection count disagrees with the immutable fixture ledger");
        }
    }

    private static void VerifyRow(BsonDocument row, string key, int ordinal, string keyField, bool updated)
    {
        if (!IsExpectedRow(row, key, ordinal, keyField, updated))
        {
            throw new InvalidOperationException($"indexed lookup returned another row for {key}");
        }
    }

    private static bool IsExpectedRow(BsonDocument row, string key, int ordinal, string keyField, bool updated)
    {
        return row[keyField].AsString == key && row["ordinal"].AsInt32 == ordinal &&
            row["payload"].AsString == Payload(ordinal, updated);
    }

    private static BsonDocument CreatePrimaryRow(string key, int index, bool updated)
    {
        return new BsonDocument
        {
            ["_id"] = key,
            ["ordinal"] = index,
            ["payload"] = Payload(index, updated)
        };
    }

    private static string Payload(int index, bool updated) => $"ledger:{(updated ? "updated" : "original")}:{index:D3}:{Keys[index]}";

    private static bool IsActionableRejection(LiteException rejection)
    {
        var message = rejection.ToString();
        var identifiesCause = message.Contains("collation", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("globalization", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("sort", StringComparison.OrdinalIgnoreCase);
        return identifiesCause && message.Contains("rebuild", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertUnchanged(string path, byte[] before)
    {
        if (!before.SequenceEqual(File.ReadAllBytes(path)))
        {
            throw new InvalidOperationException("rejected/read-only probe modified the database");
        }
    }
}
