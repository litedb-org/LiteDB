using System.IO.Compression;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3124_ModernJournalLegacyPrimary;

// Deliberate data-only restore skew. Public caller-stream failure captures a
// valid modern checkpoint journal without depending on engine test hooks.
internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")?
            .GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(ReproContext.FromEnvironment().SharedDatabaseRoot ?? Path.GetTempPath(),
            "modern-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var outcomes = new[] { Run(directory, null, host), Run(directory, "wal-secret", host) };
            Require(outcomes.Distinct().Count() == 1, "Inconsistent plain/encrypted outcomes");
            var code = outcomes[0];
            host.SendResult(code == 0, code == 0
                ? "REPRODUCED: modern journal overwrote legacy primary and truncated WAL on failed opens."
                : "FIXED: modern journal refuses legacy primary without mutation and matching modern data recovers.");
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, "INCONCLUSIVE: " + error);
            return 2;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static int Run(string directory, string? password, ReproHostClient host)
    {
        var filename = Path.Combine(directory, password == null ? "plain.db" : "encrypted.db");
        var logname = Path.Combine(directory, password == null ? "plain-log.db" : "encrypted-log.db");
        var legacy = Fixture(password);
        using var data = new FailingData();
        data.Write(legacy, 0, legacy.Length);
        using var log = new MemoryStream();
        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            db.GetCollection("newrows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" });
            db.Checkpoint();
            db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            data.Armed = true;
            try { db.Checkpoint(); throw new InvalidOperationException("Checkpoint should fail"); }
            catch (IOException error) when (error.Message == "capture modern journal") { }
        }
        var modern = data.ToArray();
        var wal = log.ToArray();
        // The footer is two pages; its backed-up header carries the modern version.
        // xUnit additionally validates the journal descriptor and bound prefix.
        Require(wal.Length == (password == null ? 4 : 5) * 8192, "Unexpected journal extent");
        var allFixed = true;
        var allBug = true;
        foreach (var readOnly in new[] { false, true })
        {
            File.WriteAllBytes(filename, modern);
            File.WriteAllBytes(logname, wal);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = Open(filename, password, readOnly);
                Require(db.GetCollection("rows").FindAll().Select(x => x.ToString()).SequenceEqual(new[]
                    { new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString() }), "Original row lost");
                Require(db.GetCollection("newrows").FindAll().Select(x => x.ToString()).SequenceEqual(new[]
                    { new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString() }), "Modern-only collection lost");
            }
            File.WriteAllBytes(filename, legacy);
            File.WriteAllBytes(logname, wal);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Exception? failure = null;
                try { using var db = Open(filename, password, readOnly); _ = db.GetCollection("rows").Count(); }
                catch (Exception error) { failure = error; }
                var dataSame = File.ReadAllBytes(filename).SequenceEqual(legacy);
                var walSame = File.Exists(logname) && File.ReadAllBytes(logname).SequenceEqual(wal);
                var truncated = File.Exists(logname) && new FileInfo(logname).Length < wal.Length;
                host.SendLog($"encrypted={password != null} readOnly={readOnly} attempt={attempt + 1} dataSame={dataSame} walSame={walSame} truncated={truncated} error={failure}");
                allFixed &= failure is LiteException refusal && refusal.ErrorCode == LiteException.INVALID_DATABASE &&
                    refusal.Message.Contains("modern header journal") && refusal.Message.Contains("preserve both files for recovery") && dataSame && walSame;
                allBug &= failure is EndOfStreamException && (readOnly ? dataSame && walSame : !dataSame && truncated);
            }
        }
        Require(allFixed || allBug, "Neither exact destructive-journal behavior nor preserving refusal occurred");
        return allBug ? 0 : 1;
    }

    private static LiteDatabase Open(string filename, string? password, bool readOnly) => new(new ConnectionString
    {
        Filename = filename, Password = password, ReadOnly = readOnly, LegacyIndexScan = readOnly
    });

    private static byte[] Fixture(string? password)
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("LegacyWal_5_0_21.zip")!;
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        using var input = zip.GetEntry(password == null ? "plain.db" : "encrypted.db")!.Open();
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FailingData : MemoryStream
    {
        internal bool Armed;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Armed && count == 8192)
            {
                Armed = false;
                throw new IOException("capture modern journal");
            }
            base.Write(buffer, offset, count);
        }
    }
}
