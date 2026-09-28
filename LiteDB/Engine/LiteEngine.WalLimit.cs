using System.IO;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Decision 4 of docs/decisions/durability-policy.md: while the data file cannot sync, the log keeps
        /// every commit and grows. Past <see cref="EngineSettings.WalLimit"/> a write that starts throws before
        /// it changes anything; reads keep working. A data sync that succeeds lifts it (the next checkpoint
        /// drains the log), so each refused write tries one first.
        /// </summary>
        private void RequireWalBelowLimit(EngineState state)
        {
            var log = _disk.GetFileLength(FileOrigin.Log);
            // Only a WAL a checkpoint can drain may pass the limit: its data file syncs, and its log too.
            if (log <= _settings.WalLimit || (!_disk.LogKnownUnsyncable && (_disk.DataSyncConfirmed || this.DataFileSyncs(state)))) return;
            throw new IOException(
                $"Cannot modify this database now: its log file ({log / (1024 * 1024)} MB) passed the WAL limit " +
                $"({_settings.WalLimit / (1024 * 1024)} MB) while the data file cannot sync to the device (#2242), so no " +
                "checkpoint can move the log into it. Reads keep working, and writes resume once the data file syncs. " +
                "The limit is set with \"wal limit\".");
        }

        /// <summary>
        /// Sync the data file for an operation of the caller's that is not a checkpoint (the WAL limit, a
        /// rebuild): false when it answers "cannot sync" (#2242). A sync that fails (an I/O error) is a
        /// write failure (decision 6): it is recorded, the engine stops (its next call reopens it
        /// read-only), and the operation that tried it throws it.
        /// </summary>
        private bool DataFileSyncs(EngineState state)
        {
            try { return _disk.DataFileSyncs(); }
            catch (IOException ex)
            {
                state.StopAfter("A data sync", WriteFailure.InFile(ex, FileOrigin.Data));
                throw;
            }
        }
    }
}
