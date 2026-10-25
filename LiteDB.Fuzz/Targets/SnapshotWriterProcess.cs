using System.Diagnostics;
using System.Reflection;

namespace LiteDB.Fuzz.Targets;

/// <summary>A killable writer keeps blocked engine calls out of the reader-control process.</summary>
internal sealed class SnapshotWriterProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly Action _onFailure;
    private bool _failed;
    internal int ProcessId => _process.Id;

    internal SnapshotWriterProcess(string database, Action onFailure = null)
    {
        _onFailure = onFailure;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location,
            "--child", "snapshot-writer", "--database", database }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start snapshot writer.");
        _errors = _process.StandardError.ReadToEndAsync();
    }

    /// <summary>Complete a commit or checkpoint while readers remain alive, or kill the stalled writer.</summary>
    internal void Execute(string command, TimeSpan? timeout = null)
    {
        try
        {
            Exchange().WaitAsync(timeout ?? TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _failed = true;
            this.Kill();
            _onFailure?.Invoke();
            throw new FuzzFailureException("SNAPSHOT_WRITER_TIMEOUT", "Writer did not progress while snapshots were live.");
        }
        catch
        {
            _failed = true;
            this.Kill();
            _onFailure?.Invoke();
            throw;
        }

        async Task Exchange()
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
            if (await _process.StandardOutput.ReadLineAsync() != "done")
                throw new FuzzFailureException("SNAPSHOT_WRITER_FAILED", await _errors);
        }
    }

    /// <summary>Stop the child before callers dispose readers or inspect the database.</summary>
    private void Kill()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        if (!_process.WaitForExit(5000))
            throw new FuzzFailureException("SNAPSHOT_WRITER_KILL_TIMEOUT", "The terminated snapshot writer did not exit.");
    }

    public void Dispose()
    {
        try
        {
            if (!_failed && !_process.HasExited)
            {
                this.Execute("stop");
                if (!_process.WaitForExit(5000))
                    throw new FuzzFailureException("SNAPSHOT_WRITER_EXIT_TIMEOUT", "Snapshot writer did not close.");
                if (_process.ExitCode != 0)
                    throw new FuzzFailureException("SNAPSHOT_WRITER_FAILED", _errors.GetAwaiter().GetResult());
            }
        }
        finally { try { this.Kill(); } finally { _process.Dispose(); } }
    }

    /// <summary>Run all transaction work synchronously on the child thread that owns it.</summary>
    internal static int RunChild(FuzzOptions options)
    {
        using var db = new LiteDatabase(new ConnectionString
        {
            Filename = options.Database, Connection = ConnectionType.Shared, TransactionPageLimit = 3
        });
        var rows = db.GetCollection("rows");
        while (Console.ReadLine() is string command)
        {
            if (command == "stop") { Console.WriteLine("done"); return 0; }
            if (command == "checkpoint") db.Checkpoint();
            else
            {
                var changes = BsonSerializer.Deserialize(Convert.FromBase64String(command))["changes"].AsArray;
                db.BeginTrans();
                try
                {
                    foreach (var value in changes)
                    {
                        var change = value.AsDocument;
                        if (change["delete"].IsBoolean && change["delete"].AsBoolean) rows.Delete(change["_id"]);
                        else rows.Upsert(change["document"].AsDocument);
                    }
                    db.Commit();
                }
                catch { db.Rollback(); throw; }
            }
            Console.WriteLine("done");
        }
        return 0;
    }
}
