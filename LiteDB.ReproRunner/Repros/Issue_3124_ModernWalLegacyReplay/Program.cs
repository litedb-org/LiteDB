using System.IO.Compression;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3124_ModernWalLegacyReplay;

// Black-box, bounded reproduction: all replayed page IDs are 3. The data and modern WAL
// are deliberately mismatched; this is corrupt-input evidence, not a synced-crash claim.
internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var directory = Path.Combine(ReproContext.FromEnvironment().SharedDatabaseRoot ?? Path.GetTempPath(),
            "fwalfmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        try
        {
            var code = Run(directory, host);
            host.SendResult(code == 0, code == 0
                ? "REPRODUCED: modern page 3 overwrote legacy data and the WAL was deleted."
                : "FIXED: both refusals preserved both files; matching modern data recovered all rows.");
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, "INCONCLUSIVE: " + error, new { Exception = error.ToString() });
            return 2;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static int Run(string directory, ReproHostClient host)
    {
        var path = Path.Combine(directory, "repro.db");
        var log = Path.Combine(directory, "repro-log.db");
        var legacy = Fixture();
        Require(legacy[59] == 8, "Expected authentic v8 fixture");
        File.WriteAllBytes(path, legacy);
        byte[] modern, wal;
        string[] expected;
        using (var db = new LiteDatabase(path))
        {
            db.CheckpointSize = 0;
            db.Checkpoint();
            var rows = db.GetCollection("rows");
            expected = Rows(db);
            Require(expected.Length == 48, "Expected 48 fixture rows");
            rows.Update(rows.FindAll().First());
            modern = ReadShared(path);
            wal = ReadShared(log).Take(8192).ToArray();
        }
        Require(wal.Length == 8192 && BitConverter.ToUInt32(wal, 0) == 3 && wal[18] == 1 && wal[31] == 0xA5,
            "Expected one confirmed modern page 3, with its first trailer absent");
        File.WriteAllBytes(path, legacy);
        File.WriteAllBytes(log, wal);
        // Legacy checkpoint normalizes transaction metadata before copying the payload.
        var replayed = (byte[])wal.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(replayed, 14);
        replayed[18] = 0;
        var refusals = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            LiteException? failure = null;
            try { using var db = new LiteDatabase(path); _ = Rows(db); }
            catch (LiteException error) { failure = error; }
            var dataSame = File.ReadAllBytes(path).SequenceEqual(legacy);
            var logSame = File.Exists(log) && File.ReadAllBytes(log).SequenceEqual(wal);
            host.SendLog($"attempt={attempt + 1} dataSame={dataSame} logSame={logSame} error={failure}");
            if (failure?.ErrorCode == LiteException.INVALID_DATABASE && failure.Message.Contains("non-legacy format") && dataSame && logSame)
                refusals++;
            else
            {
                Require(!dataSame && !File.Exists(log), "Neither expected refusal nor destructive replay occurred");
                Require(File.ReadAllBytes(path).Skip(3 * 8192).Take(8192).SequenceEqual(replayed), "Unexpected data damage");
            }
        }
        if (refusals == 0) return 0;
        Require(refusals == 2, "Inconsistent refusal on reopen");
        // Same truncated WAL with its matching checksummed header is still valid input.
        File.WriteAllBytes(path, modern);
        File.WriteAllBytes(log, wal);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var db = new LiteDatabase(path);
            Require(Rows(db).SequenceEqual(expected), "Matching modern header lost a row or payload");
        }
        return 1;
    }

    private static string[] Rows(LiteDatabase db) => db.GetCollection("rows").FindAll()
        .OrderBy(x => x["_id"]).Select(x => x.ToString()).ToArray();

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static byte[] Fixture()
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("IndexMigration_5_0_21.zip")!;
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        using var input = zip.GetEntry("plain.db")!.Open();
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
