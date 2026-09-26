using System.Diagnostics;
using System.Reflection;

namespace LiteDB.Fuzz.Targets;

internal sealed class SnapshotFuzzer : IFuzzTarget
{
    public string Name => "snapshot";
    public string Description => "Long-lived process-isolated reader snapshots across commits, page reuse, and checkpoints.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "snapshot.db"));
        using var writer = Open(file);
        var rows = writer.GetCollection("rows");
        rows.EnsureIndex("value", "Value");
        for (var id = 1; id <= 18; id++) rows.Insert(Document(id, id * 10));
        writer.Checkpoint();

        var validations = 0;
        var checkpoints = 0;
        while (context.Next())
        {
            using var firstPrimary = new ReaderProcess(context, file,
                Path.Combine(context.DirectoryPath, $"reader-{context.Steps}-first-primary"), 0);
            CommitPastReaders(writer, rows, context, firstPrimary);
            firstPrimary.Stop();
            using var secondSecondary = new ReaderProcess(context, file,
                Path.Combine(context.DirectoryPath, $"reader-{context.Steps}-second-secondary"), 1);
            CommitPastReaders(writer, rows, context, secondSecondary);
            if (context.Steps % 5 == 0)
            {
                var scratch = writer.GetCollection("scratch" + context.Steps);
                scratch.Insert(new BsonDocument { ["_id"] = 1, ["value"] = context.Steps });
                scratch.EnsureIndex("value", "value");
                writer.DropCollection(scratch.Name);
            }
            validations += 2;
            context.ObserveNovelty("snapshot", 2, checkpoints % 4, rows.Count() / 8);
            secondSecondary.Stop();
            writer.Checkpoint();
            checkpoints++;
        }

        writer.Checkpoint();
        DatabaseIntegrityVerifier.Verify(context, file);
        context.Metrics["snapshotValidations"] = validations;
        context.Metrics["checkpointsBetweenReaderGenerations"] = checkpoints;
        return Task.CompletedTask;
    }

    internal static int RunChild(FuzzOptions options)
    {
        var prefix = options.Ledger;
        using var db = Open(options.Database);
        var rows = db.GetCollection("rows");
        var expected = Snapshot(Documents(rows, options.WorkerId));
        using var cursor = Documents(rows, options.WorkerId).GetEnumerator();
        var documents = new List<BsonDocument>();
        if (cursor.MoveNext()) documents.Add(cursor.Current);
        Publish(prefix + ".ready", expected);
        while (!File.Exists(prefix + ".stop"))
        {
            if (File.Exists(prefix + ".read"))
            {
                File.Delete(prefix + ".read");
                while (cursor.MoveNext()) documents.Add(cursor.Current);
                var actual = Snapshot(documents);
                Publish(prefix + ".result", actual == expected ? "ok" : "changed");
            }
            Thread.Sleep(10);
        }
        return 0;
    }

    private static void Publish(string path, string content)
    {
        // File.Exists is the protocol signal. Publish only after the complete payload is present in
        // another directory entry so the parent can never observe a newly-created, partial file.
        var temporary = path + ".publishing";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }

    private static IEnumerable<BsonDocument> Documents(ILiteCollection<BsonDocument> rows, int mode) =>
        mode == 0 ? rows.Query().OrderBy("_id").ToEnumerable() :
        rows.Query().OrderBy("Value").ThenBy("_id").ToEnumerable();

    private static string Snapshot(IEnumerable<BsonDocument> documents)
    {
        var values = documents.Select(row => Convert.ToBase64String(BsonSerializer.Serialize(row))).ToArray();
        return string.Join("|", values) + "\n" + values.Length;
    }

    private static void Mutate(LiteDatabase writer, ILiteCollection<BsonDocument> rows, FuzzContext context)
    {
        writer.BeginTrans();
        var changes = 2 + context.Random.Next(4);
        for (var change = 0; change < changes; change++)
        {
            var id = context.Random.Next(1, 35);
            if (context.Random.Next(4) == 0) rows.Delete(id);
            else rows.Upsert(Document(id, context.Random.Next(-1000, 1001)));
        }
        writer.Commit();
    }

    private static void CommitPastReaders(LiteDatabase writer, ILiteCollection<BsonDocument> rows,
        FuzzContext context, params ReaderProcess[] readers)
    {
        var mutation = Task.Factory.StartNew(() => Mutate(writer, rows, context), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Thread.Sleep(20);
        foreach (var reader in readers) reader.Validate();
        context.Check(mutation.Wait(TimeSpan.FromSeconds(15)),
            "A writer did not resume after the old snapshot cursor completed.");
        mutation.GetAwaiter().GetResult();
    }

    private static LiteDatabase Open(string file) => new(new ConnectionString
    {
        Filename = file,
        Connection = ConnectionType.Shared,
        TransactionPageLimit = 3
    });

    private static BsonDocument Document(int id, int value) => new()
    {
        ["_id"] = id,
        ["Value"] = value,
        ["Payload"] = new byte[9000 + id % 5 * 200]
    };

    private sealed class ReaderProcess : IDisposable
    {
        private readonly Process _process;
        private readonly string _prefix;
        private readonly FuzzContext _context;
        private int _validation;
        private bool _stopped;

        internal ReaderProcess(FuzzContext context, string database, string prefix, int mode)
        {
            _context = context;
            _prefix = prefix;
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            Add("--child", "snapshot-reader");
            Add("--database", database);
            Add("--ledger", prefix);
            Add("--worker-id", mode.ToString());
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start snapshot reader.");
            WaitFor(prefix + ".ready", "Snapshot reader did not become ready.");

            void Add(string name, string value)
            {
                start.ArgumentList.Add(name);
                start.ArgumentList.Add(value);
            }
        }

        internal void Validate()
        {
            var result = _prefix + $".result-{++_validation}";
            var sharedResult = _prefix + ".result";
            File.Delete(sharedResult);
            File.WriteAllText(_prefix + ".read", "read");
            WaitFor(sharedResult, "Snapshot reader did not return a validation.");
            var value = File.ReadAllText(sharedResult);
            File.Move(sharedResult, result, true);
            FuzzOracle.VerifySnapshotResult(_context, value);
        }

        public void Dispose()
        {
            this.Stop();
        }

        internal void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            File.WriteAllText(_prefix + ".stop", "stop");
            if (!_process.WaitForExit(15000))
            {
                _process.Kill(entireProcessTree: true);
                throw new FuzzFailureException("SNAPSHOT_READER_HUNG", "Snapshot reader did not exit.");
            }
            var error = _process.StandardError.ReadToEnd();
            if (_process.ExitCode != 0)
                throw new FuzzFailureException("SNAPSHOT_READER_FAILED", error);
            _process.Dispose();
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(_prefix),
                Path.GetFileName(_prefix) + ".*"))
                File.Delete(path);
        }

        private void WaitFor(string path, string message)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!File.Exists(path) && DateTime.UtcNow < deadline)
            {
                if (_process.HasExited) break;
                Thread.Sleep(10);
            }
            if (File.Exists(path)) return;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            throw new FuzzFailureException("SNAPSHOT_PROTOCOL_TIMEOUT", message);
        }
    }
}
