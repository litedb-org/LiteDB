using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class RebuildTransitionFuzzer : IFuzzTarget
{
    private static readonly string[] Phases =
    {
        "before-log-backup", "after-log-backup", "before-source-backup",
        "after-source-backup", "before-temp-install", "after-temp-install"
    };

    public string Name => "rebuild-transition";
    public string Description => "Faults every rebuild installation transition and requires one complete recoverable state.";

    public Task RunAsync(FuzzContext context)
    {
        var faults = 0;
        while (context.Next())
        {
            var phase = Phases[(context.Steps - 1) % Phases.Length];
            var file = context.StepFile($"rebuild-{context.Steps}.db");
            using (var seed = new LiteDatabase(file))
            {
                var rows = seed.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 100).Select(id => new BsonDocument
                {
                    ["_id"] = id, ["value"] = id, ["payload"] = new byte[2000 + id]
                }));
                rows.EnsureIndex("value", "value");
                using var payload = new MemoryStream(Enumerable.Range(0, 12000).Select(index => (byte)index).ToArray());
                seed.FileStorage.Upload("rebuild-file", "payload.bin", payload);
                seed.CheckpointSize = 0;
                rows.Insert(new BsonDocument { ["_id"] = 101, ["value"] = 101 });
            }
            var changePassword = context.Steps % 2 == 0;
            var fired = false;
            RebuildService.SimulateInstallFailure = current =>
            {
                if (current != phase) return;
                fired = true;
                throw new IOException("Injected rebuild transition failure at " + phase);
            };
            try
            {
                using var db = new LiteDatabase(file);
                try { db.Rebuild(changePassword ? new RebuildOptions { Password = "new-password" } : null); }
                catch (IOException) when (fired) { faults++; }
            }
            finally { RebuildService.SimulateInstallFailure = null; }
            context.Check(fired, $"Rebuild transition {phase} was not reached.");
            LiteDatabase recovered = null;
            string recoveredPassword = null;
            foreach (var password in changePassword ? new[] { (string)null, "new-password" } : new[] { (string)null })
            {
                try
                {
                    recovered = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    recoveredPassword = password;
                    break;
                }
                catch (LiteException) { }
            }
            context.Check(recovered != null, "Interrupted password-changing rebuild left no openable complete file.");
            using (recovered)
            {
                var rows = recovered.GetCollection("rows");
                context.Check(rows.Count() == 101 && rows.FindById(101)?["value"] == 101,
                    $"Rebuild transition failure at {phase} lost a complete state.");
                context.Check(rows.Query().OrderBy("value").ToArray().Length == 101,
                    $"Rebuild transition failure at {phase} damaged the rebuilt index.");
                using var payload = new MemoryStream();
                recovered.FileStorage.Download("rebuild-file", payload);
                context.Check(payload.Length == 12000,
                    $"Rebuild transition failure at {phase} damaged FileStorage.");
                recovered.Checkpoint();
            }
            DatabaseIntegrityVerifier.Verify(context, file, recoveredPassword);
            context.ObserveNovelty("rebuild-transition", phase, changePassword, recoveredPassword != null);
        }
        if (context.Steps >= Phases.Length)
            context.Check(faults == context.Steps, "Rebuild transition campaign missed a configured fault.");
        context.Metrics["rebuildTransitionFaults"] = faults;
        return Task.CompletedTask;
    }
}
