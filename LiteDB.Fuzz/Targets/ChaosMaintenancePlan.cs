namespace LiteDB.Fuzz.Targets;

/// <summary>The maintenance a second thread runs while (or before) the active operation runs.</summary>
internal enum MaintenanceKind
{
    Close,
    Rebuild,
    Fatal
}

/// <summary>The operation in progress on the active thread when the maintenance starts (or arrives).</summary>
internal enum ActiveKind
{
    Bulk,
    Reader,
    Transaction,
    Checkpoint,
    Rebuild
}

/// <summary>
/// ActiveFirst: the active operation is paused at its forced point when the maintenance starts.
/// MaintenanceFirst: the maintenance is paused at its forced point when the active operation starts.
/// </summary>
internal enum MaintenanceOrder
{
    ActiveFirst,
    MaintenanceFirst
}

/// <summary>
/// One chaos-maintenance scenario, drawn from fuzz randomness only and always with the same number
/// of draws, so the recorded input never depends on what a schedule did. Its trace line is a pure
/// function of the draws: outcomes decided by native scheduling stay out of <c>trace.jsonl</c>.
/// </summary>
internal sealed record MaintenancePlan(bool Shared, MaintenanceKind Maintenance, ActiveKind Active,
    MaintenanceOrder Order, int SeedRows, int Writes, int Pause, bool Upsert, bool Commit, bool InWal)
{
    internal static MaintenancePlan Draw(Random random)
    {
        var shared = random.Next(2) == 1;
        var maintenance = (MaintenanceKind)random.Next(3);
        var active = (ActiveKind)random.Next(5);
        var order = (MaintenanceOrder)random.Next(2);
        // Rows carry 3000-character payloads: above about 22 rows a Shared query result passes 64 KiB and streams under a reader lease.
        var seedRows = random.Next(8, 40);
        var writes = random.Next(2, 8);
        var pause = random.Next(1, writes + 1);
        var upsert = random.Next(2) == 0;
        var commit = random.Next(4) != 0;
        var inWal = random.Next(3) == 0;
        return new MaintenancePlan(shared, maintenance, active, order, seedRows, writes, pause, upsert, commit, inWal);
    }

    internal string Mode => this.Shared ? "shared" : "direct";

    internal string Dimension =>
        $"mode={this.Mode};maintenance={Name(this.Maintenance)};active={Name(this.Active)};order={Name(this.Order)}" +
        (this.PausesInWal ? ";pause=wal-write" : "");

    /// <summary>The bulk operation's class name in outcomes.jsonl.</summary>
    internal string BulkOp => this.Upsert ? "Upsert" : "Insert";

    /// <summary>
    /// The active write pauses inside its commit's first WAL page write (holding the commit's header and
    /// WAL writer locks) instead of at its input or between calls: bulk writes, and committing transactions.
    /// </summary>
    internal bool PausesInWal => this.InWal && (this.Active == ActiveKind.Bulk || this.Active == ActiveKind.Transaction && this.Commit);

    /// <summary>Rows the reader reads before its forced point (at least one, at most every row).</summary>
    internal int ReaderPause => Math.Min(this.Pause, this.SeedRows);

    internal object TraceDetail => new
    {
        mode = this.Mode, maintenance = Name(this.Maintenance), active = Name(this.Active), order = Name(this.Order),
        seedRows = this.SeedRows, writes = this.Writes, pause = this.Pause, upsert = this.Upsert, commit = this.Commit,
        inWal = this.PausesInWal
    };

    internal static string Name<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        var builder = new System.Text.StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i])) builder.Append('-');
            builder.Append(char.ToLowerInvariant(text[i]));
        }
        return builder.ToString();
    }
}
