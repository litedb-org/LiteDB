using System;
using LiteDB;

/// <summary>
/// Operations measured on one connection. Common workloads use only pre-handle APIs; handle
/// workloads are compiled only with HANDLES and are compared with the legacy adapter named
/// for them in scripts/measure-transaction-paths.py, on the same binary.
/// </summary>
internal static class Workloads
{
    /// <summary>Run a single-connection workload; throws for an unknown name.</summary>
    public static bool RunSingle(RunSettings settings, JsonRecord record)
    {
        // Direct mode opens the file exclusively, so Direct open/close keeps no session open.
        var keepSession = settings.Workload != "direct-open-close";
        using var db = keepSession ? new LiteDatabase(settings.Connection) : null;
        var operation = Create(settings.Workload, db, settings.Connection)
            ?? throw new ArgumentException("Unknown workload " + settings.Workload);
        Latency.Measure(operation, settings, record);
        return true;
    }

    private static Action Create(string workload, LiteDatabase db, ConnectionString connection)
    {
        var rows = db?.GetCollection("rows");
        var next = 0;
        switch (workload)
        {
            case "direct-ordinary-read":
            case "shared-ordinary-read":
                return () => Read(rows, 1 + (next++ % Bench.Rows));
            case "direct-legacy-read":
            case "shared-legacy-read":
                return () =>
                {
                    db.BeginTrans();
                    Read(rows, 1 + (next++ % Bench.Rows));
                    db.Commit();
                };
            case "direct-ordinary-update":
            case "shared-ordinary-update":
                return () => Update(rows, 1 + (next++ % Bench.Rows));
            case "direct-legacy-update":
            case "shared-legacy-update":
                return () =>
                {
                    db.BeginTrans();
                    Update(rows, 1 + (next++ % Bench.Rows));
                    db.Commit();
                };
            case "shared-legacy-empty":
                return () =>
                {
                    db.BeginTrans();
                    db.Commit();
                };
            case "direct-open-close":
            case "shared-open-close":
                // Attach, count, dispose; in Shared mode the measured session stays open meanwhile.
                return () =>
                {
                    using var attached = new LiteDatabase(connection);
                    if (attached.GetCollection("rows").Count() != Bench.Rows) throw new InvalidOperationException("Row count changed");
                };
        }
#if HANDLES
        return HandleWorkloads.Create(workload, db, () => 1 + (next++ % Bench.Rows));
#else
        return null;
#endif
    }

    /// <summary>A contender's operation: <paramref name="kind"/> updates of its own row on its own connection.</summary>
    public static Action Contender(string kind, LiteDatabase db, int id)
    {
        var rows = db.GetCollection("rows");
        switch (kind)
        {
            case "ordinary":
                return () => Update(rows, id);
            case "legacy":
                return () =>
                {
                    db.BeginTrans();
                    Update(rows, id);
                    db.Commit();
                };
        }
#if HANDLES
        if (kind == "handle") return HandleWorkloads.Contender(db, id);
#endif
        throw new ArgumentException("Unknown contention kind " + kind);
    }

    public static void Read(ILiteCollection<BsonDocument> rows, int id)
    {
        if (rows.FindById(id) == null) throw new InvalidOperationException("Missing row");
    }

    public static void Update(ILiteCollection<BsonDocument> rows, int id)
    {
        if (!rows.Update(new BsonDocument { ["_id"] = id, ["value"] = id })) throw new InvalidOperationException("Missing row");
    }
}

#if HANDLES
/// <summary>Candidate-only operations through <see cref="ILiteTransaction"/> handles.</summary>
internal static class HandleWorkloads
{
    public static Action Create(string workload, LiteDatabase db, Func<int> next)
    {
        switch (workload)
        {
            case "direct-handle-read":
            case "shared-handle-read":
                return () =>
                {
                    using var transaction = db.BeginTransaction();
                    Workloads.Read(transaction.GetCollection("rows"), next());
                    transaction.Commit();
                };
            case "direct-handle-update":
            case "shared-handle-update":
                return () =>
                {
                    using var transaction = db.BeginTransaction();
                    Workloads.Update(transaction.GetCollection("rows"), next());
                    transaction.Commit();
                };
            case "shared-handle-empty":
                return () =>
                {
                    using var transaction = db.BeginTransaction();
                    transaction.Commit();
                };
            default:
                return null;
        }
    }

    public static Action Contender(LiteDatabase db, int id) => () =>
    {
        using var transaction = db.BeginTransaction();
        Workloads.Update(transaction.GetCollection("rows"), id);
        transaction.Commit();
    };
}
#endif
