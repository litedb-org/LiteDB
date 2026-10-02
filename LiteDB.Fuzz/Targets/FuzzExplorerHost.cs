using System.Diagnostics;
using LiteDB.ConcurrencyTesting;
using LiteDB.Tests.Safety;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// The concurrency explorer's host inside the fuzz runner: every explorer oracle goes through
/// <see cref="FuzzOracles"/>, so explorer runs write the same evidence files as every other target
/// (<c>outcomes.jsonl</c>, <c>connection-clean.jsonl</c>, <c>quiescent.jsonl</c>, <c>faults.jsonl</c>)
/// and fail with the same target-qualified ids (DEADLINE_, OWNERSHIP_, CONNECTION_CLEAN_, QUIESCENT_,
/// DURABLE_, FAULT_). An operation past its deadline is reported by the runner's watchdog, which
/// records the run and exits, so <see cref="Overdue"/> never has anything to return.
/// </summary>
internal sealed class FuzzExplorerHost : IExplorerHost
{
    private readonly FuzzContext _context;

    internal FuzzExplorerHost(FuzzContext context)
    {
        _context = context;
        context.Oracles.PragmaTimeout = ExplorerSchedule.PragmaTimeout;
    }

    public void Execute(string op, string dimension, TimeSpan deadline, Action call) =>
        _context.Deadline(op, call, dimension, deadline);

    public ExplorerFailure Overdue() => null;

    public void WatchOwnership() => _context.Oracles.WatchOwnership();

    public void Ownership(SharedEngine connection, string point)
    {
        if (connection == null) _context.Oracles.ThrowLatched();
        else _context.Ownership(connection, point);
    }

    public void ConnectionClean(object connection, string op) => _context.ConnectionClean(connection, op);

    public void Quiescent(string path, string point) => _context.Quiescent(path, point);

    public void Durable(DurableLedger ledger, ILiteDatabase reopened, string point) => _context.Durable(ledger, reopened, point);

    public string FailureId(Exception error) => error is FuzzFailureException fuzz
        ? fuzz.FailureId ?? FailureIdentity.Get(error)
        : ExplorerRun.DefaultFailureId(error);

    public void FaultReached(string fault, Exception injected, bool required) => _context.FaultReached(fault, injected, required);

    public void FaultDisposed(string op, Exception injected, FaultDisposition declared, Exception thrown) =>
        _context.FaultDisposed(op, injected, declared, thrown);

    /// <summary>This fuzz executable in <c>--child explorer-writer</c> mode.</summary>
    public ProcessStartInfo ExternalWriter(string path)
    {
        var assembly = typeof(FuzzExplorerHost).Assembly.Location;
        var start = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet");
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet") start.ArgumentList.Add(assembly);
        foreach (var argument in new[] { "--child", ExplorerWriterChild.Mode, "--database", path }) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// The id a target raises for an explorer failure: oracle ids from <see cref="FuzzOracles"/> are
    /// already target-qualified; the explorer's own (EXPLORER_*, UNEXPECTED_EXCEPTION_*) get the target prefix.
    /// </summary>
    internal static string Qualified(string target, string failureId)
    {
        var prefix = FuzzOracles.Safe(target) + "_";
        return failureId.Contains(prefix, StringComparison.Ordinal) ? failureId : prefix + failureId;
    }

    public void Dispose()
    {
    }
}
