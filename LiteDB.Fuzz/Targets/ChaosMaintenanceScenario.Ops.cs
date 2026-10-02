using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>The operations of both scenario threads and what each acknowledged.</summary>
internal sealed partial class ChaosMaintenanceScenario
{
    private const int InsertBase = 1000;
    private const int TransactionBase = 2000;
    private const int FatalBase = 3000;

    private string Dimension => _plan.Dimension;

    /// <param name="beforePoint">The call completes before the active thread's forced point.</param>
    private bool Call(ScenarioThread thread, string op, Action call, bool beforePoint = false) => thread.Call(_context, op, call,
        this.Dimension, ChaosMaintenanceDeclarations.Permitted(_plan, thread.Role, op, beforePoint), OpDeadline);

    private void ActiveBody()
    {
        var rows = _db.GetCollection("rows");
        switch (_plan.Active)
        {
            case ActiveKind.Bulk:
                this.Bulk(rows);
                break;
            case ActiveKind.Reader:
                this.Call(_active, "Reader", () => this.Read(rows));
                break;
            case ActiveKind.Transaction:
                this.Transaction(rows);
                break;
            case ActiveKind.Checkpoint:
                this.Call(_active, "Checkpoint", () => _db.Checkpoint());
                break;
            case ActiveKind.Rebuild:
                this.PauseSharedRebuild();
                this.Call(_active, "Rebuild", () => _db.Rebuild());
                break;
        }
    }

    private void MaintenanceBody()
    {
        switch (_plan.Maintenance)
        {
            case MaintenanceKind.Close:
                if (this.Call(_maintenance, "Dispose", () => _db.Dispose())) _context.ConnectionClean(_db, "Dispose");
                break;
            case MaintenanceKind.Rebuild:
                if (_plan.Order == MaintenanceOrder.MaintenanceFirst) this.PauseSharedRebuild();
                this.Call(_maintenance, "Rebuild", () => _db.Rebuild());
                break;
            case MaintenanceKind.Fatal:
                this.FatalWrite();
                break;
        }
    }

    /// <summary>An insert or upsert whose lazy input pauses before its <c>Pause</c>-th document.</summary>
    private void Bulk(ILiteCollection<BsonDocument> rows)
    {
        // Upsert rewrites seed rows 1..Writes; insert adds new ids.
        var before = Enumerable.Range(1, _plan.Writes)
            .Select(i => _plan.Upsert ? Row(i, i) : null).ToArray();
        var after = Enumerable.Range(1, _plan.Writes)
            .Select(i => _plan.Upsert ? Row(i, InsertBase + i) : Row(InsertBase + i, i)).ToArray();
        var consumed = 0;
        IEnumerable<BsonDocument> Input()
        {
            for (var i = 0; i < after.Length; i++)
            {
                if (i == 0) this.HookSharedCore();
                if (i + 1 == _plan.Pause && !_plan.PausesInWal) _activePoint.Pause();
                consumed++;
                yield return new BsonDocument(after[i]);
            }
        }
        var ok = this.Call(_active, _plan.BulkOp, () =>
        {
            if (_plan.Upsert) rows.Upsert(Input());
            else rows.Insert(Input());
        });
        if (ok) this.Acknowledge("rows", after);
        // Refused before its input was read: nothing of it was written.
        else if (consumed == 0 && _plan.Upsert) this.Acknowledge("rows", before);
        else if (consumed == 0) this.Abort("rows", after, before);
        else this.Uncertain.Add(new UncertainWrite("rows", Ids(after), before, after));
    }

    /// <summary>A query over every seed row, paused after <c>ReaderPause</c> rows; checks its snapshot when it completes.</summary>
    private void Read(ILiteCollection<BsonDocument> rows)
    {
        var read = new List<int>();
        using (var reader = rows.FindAll().GetEnumerator())
        {
            while (reader.MoveNext())
            {
                read.Add(reader.Current["_id"].AsInt32);
                if (read.Count == _plan.ReaderPause) _activePoint.Pause();
            }
        }
        // Concurrent maintenance writes other collections or keeps the content (rebuild): the snapshot is the seed.
        if (!read.SequenceEqual(Enumerable.Range(1, _plan.SeedRows)))
            throw new FuzzFailureException("CHAOS_MAINTENANCE_READER_SNAPSHOT",
                $"A reader that completed read [{string.Join(",", read.Take(50))}] instead of ids 1..{_plan.SeedRows} ({this.Dimension}).");
    }

    /// <summary>BeginTrans, <c>Writes</c> upserts (paused after the <c>Pause</c>-th), then Commit or Rollback, all on one thread.</summary>
    private void Transaction(ILiteCollection<BsonDocument> rows)
    {
        var after = Enumerable.Range(1, _plan.Writes).Select(i => Row(TransactionBase + i, i)).ToArray();
        var before = new BsonDocument[after.Length];
        var pauseBetweenCalls = !_plan.PausesInWal;
        if (!this.Call(_active, "BeginTrans", () => _db.BeginTrans(), beforePoint: true))
        {
            this.Abort("rows", after, before);
            return;
        }
        var written = 0;
        for (var i = 0; i < after.Length; i++)
        {
            var beforePoint = !pauseBetweenCalls || i < _plan.Pause;
            if (!this.Call(_active, "TransactionUpsert", () => rows.Upsert(new BsonDocument(after[i])), beforePoint))
            {
                this.Uncertain.Add(new UncertainWrite("rows", Ids(after), before, after.Take(written).Concat(before.Skip(written)).ToArray()));
                return;
            }
            written++;
            if (written == _plan.Pause && pauseBetweenCalls) _activePoint.Pause();
        }
        // The transaction's core stays attached between its calls.
        if (_plan.PausesInWal) this.HookSharedCore();
        var completed = false;
        var ok = this.Call(_active, _plan.Commit ? "Commit" : "Rollback",
            () => completed = _plan.Commit ? _db.Commit() : _db.Rollback());
        if (ok && completed && _plan.Commit) this.Acknowledge("rows", after);
        else if (ok) this.Abort("rows", after, before);
        else this.Uncertain.Add(new UncertainWrite("rows", Ids(after), before, after));
    }

    /// <summary>
    /// A write to its own collection whose first WAL page write fails (skip model: the write never
    /// happens). That I/O failure stops a Direct engine, or the Shared operation core that ran it.
    /// Maintenance first, it pauses before its first document, inside its transaction.
    /// </summary>
    private void FatalWrite()
    {
        var documents = new[] { Row(FatalBase + 1, 1), Row(FatalBase + 2, 2) };
        IEnumerable<BsonDocument> Input()
        {
            this.ArmFault();
            if (_plan.Order == MaintenanceOrder.MaintenanceFirst) _maintenancePoint.Pause();
            foreach (var document in documents) yield return new BsonDocument(document);
        }
        var ok = this.Call(_maintenance, "FatalWrite", () => _db.GetCollection("fatal").Insert(Input()));
        // The failed WAL write precedes the commit's confirmation, so the write never committed.
        if (ok) this.Acknowledge("fatal", documents);
        else this.Abort("fatal", documents, new BsonDocument[documents.Length]);
    }

    /// <summary>Arm the WAL-write fault on the engine serving this call (the Shared core is attached during the call).</summary>
    private void ArmFault()
    {
        _fatalAdmitted = true;
        _faultArmed = true;
        this.ServingEngine().SimulateDiskWriteFail = this.OnWalWrite;
    }

    /// <summary>Shared, from inside a call of the active thread: its core gets the WAL-write pause.</summary>
    private void HookSharedCore()
    {
        if (_plan.PausesInWal && _engine is SharedEngine) this.ServingEngine().SimulateDiskWriteFail = this.OnWalWrite;
    }

    private LiteEngine ServingEngine() => _engine as LiteEngine ?? SharedInternals.Core((SharedEngine)_engine)
        ?? throw new FuzzFailureException("CHAOS_MAINTENANCE_NO_CORE", "A Shared call ran without an attached core.");

    /// <summary>Shared: the next core this connection opens (the rebuild's) gets the exclusive-admission pause.</summary>
    private void PauseSharedRebuild()
    {
        if (_engine is not SharedEngine shared) return;
        var settings = SharedInternals.Settings(shared);
        shared.SimulateOpenEngine = () =>
        {
            shared.SimulateOpenEngine = null;
            var core = new LiteEngine(settings);
            core.SimulateAfterExclusiveAdmission = this.OnExclusiveAdmitted;
            return core;
        };
    }

    private static BsonValue[] Ids(BsonDocument[] documents) => documents.Select(document => document["_id"]).ToArray();

    private void Acknowledge(string collection, BsonDocument[] documents)
    {
        lock (this.Ledger) foreach (var document in documents) this.Ledger.Acknowledge(collection, document["_id"], document);
    }

    private void Abort(string collection, BsonDocument[] after, BsonDocument[] before)
    {
        lock (this.Ledger)
            for (var i = 0; i < after.Length; i++) this.Ledger.Abort(collection, after[i]["_id"], before[i]);
    }
}
