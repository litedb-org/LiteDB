using System.Security.Cryptography;
using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class CompatibilityFuzzer : IFuzzTarget
{
    public string Name => "compatibility";
    public string Description => "Legacy v4 upgrade, encryption, dirty-WAL, index, storage, and read-only compatibility states.";

    public Task RunAsync(FuzzContext context)
    {
        var cases = new[] { ("v4.db", (string)null), ("Issue_2494_EncryptedV4.db", "pass123") };
        var upgrades = 0;
        while (context.Next())
        {
            var legacy = cases[(context.Steps - 1) % cases.Length];
            Exercise(context, legacy.Item1, legacy.Item2);
            upgrades++;
            context.ObserveNovelty("legacy-upgrade", legacy.Item1, legacy.Item2 != null, context.Steps % 3);
        }
        context.Metrics["legacyUpgradeCases"] = upgrades;
        return Task.CompletedTask;
    }

    private static void Exercise(FuzzContext context, string resource, string password)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Corpus", "Legacy", resource);
        context.Check(File.Exists(source), $"Missing legacy corpus resource {resource}.");
        InterruptedUpgrade(context, source, password);
        var file = context.StepFile($"legacy-{context.Steps}-{resource}");
        File.Copy(source, file, true);
        var original = Hash(file);

        if (password != null)
        {
            Exception wrongPassword = null;
            try { using var ignored = Open(file, "wrong-password", upgrade: true, readOnly: false); }
            catch (Exception error) { wrongPassword = error; }
            context.Check(wrongPassword is LiteException or ArgumentException,
                "Encrypted legacy file accepted a wrong password or leaked an internal exception.");
            context.Check(original.SequenceEqual(Hash(file)), "Failed encrypted upgrade changed source bytes.");
        }

        string collection;
        using (var readOnly = Open(file, password, upgrade: true, readOnly: true))
        {
            collection = readOnly.GetCollectionNames().FirstOrDefault(name => readOnly.GetCollection(name).Count() > 0);
            context.Check(collection != null, "Read-only legacy upgrade exposed no documents.");
        }
        context.Check(!original.SequenceEqual(Hash(file)), "Explicit legacy upgrade did not rewrite the legacy file.");

        using (var upgraded = Open(file, password, upgrade: false, readOnly: false))
        {
            var rows = upgraded.GetCollection(collection);
            context.Check(rows.Count() > 0, "Writable legacy upgrade lost documents.");
            rows.EnsureIndex("fuzz_name", "$._id");
            using var payload = new MemoryStream(Enumerable.Range(0, 9000).Select(i => (byte)i).ToArray());
            upgraded.FileStorage.Upload("compat", "compat.bin", payload,
                new BsonDocument { ["source"] = resource });
            upgraded.CheckpointSize = 0;
            rows.Upsert(new BsonDocument { ["_id"] = "fuzz-dirty", ["value"] = context.Steps });
        }

        using (var reopened = Open(file, password, upgrade: false, readOnly: false))
        {
            context.Check(reopened.GetCollection(collection).FindById("fuzz-dirty") != null,
                "Upgraded dirty WAL lost its committed document.");
            using var output = new MemoryStream();
            reopened.FileStorage.Download("compat", output);
            context.Check(output.Length == 9000, "Upgraded FileStorage payload changed.");
            reopened.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file, password);
        context.Trace("compatibility", new { resource, encrypted = password != null });
    }

    private static void InterruptedUpgrade(FuzzContext context, string source, string password)
    {
        var phases = new[] { "before-log-backup", "after-log-backup", "before-source-backup",
            "after-source-backup", "before-temp-install", "after-temp-install" };
        var phase = phases[(context.Steps - 1) % phases.Length];
        var file = context.StepFile(
            $"interrupted-{context.Steps}-{Path.GetFileName(source)}");
        File.Copy(source, file, true);
        var fired = false;
        RebuildService.SimulateInstallFailure = current =>
        {
            if (current != phase) return;
            fired = true;
            throw new IOException("Injected legacy upgrade publication failure.");
        };
        try
        {
            try { using var ignored = Open(file, password, upgrade: true, readOnly: false); }
            catch (IOException) when (fired) { }
        }
        finally { RebuildService.SimulateInstallFailure = null; }
        context.Check(fired, $"Legacy upgrade did not reach publication phase {phase}.");
        using (var recovered = Open(file, password, upgrade: true, readOnly: false))
        {
            context.Check(recovered.GetCollectionNames().Any(name => recovered.GetCollection(name).Count() > 0),
                $"Legacy upgrade could not resume after failure at {phase}.");
            recovered.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file, password);
    }

    private static LiteDatabase Open(string file, string password, bool upgrade, bool readOnly) => new(new ConnectionString
    {
        Filename = file, Password = password, Upgrade = upgrade, ReadOnly = readOnly, DurableCommits = true
    });

    private static byte[] Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }
}
