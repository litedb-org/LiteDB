using System.IO;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Decision 4 of docs/decisions/durability-policy.md: while the data file cannot sync, the log keeps
        /// every commit and grows; so it does while the log cannot sync (a checkpoint writes nothing behind
        /// it). Past <see cref="EngineSettings.WalLimit"/> a write that starts throws before it changes
        /// anything; reads keep working. A sync that succeeds lifts it (the next checkpoint drains the log),
        /// so each refused write tries one first. The check runs before the writer is taken (a transaction
        /// already running may still commit); its syncs recheck for a published failure once they hold
        /// the lock that orders them, and record a real I/O failure before they release it (#3052).
        /// </summary>
        private void RequireWalBelowLimit(EngineState state)
        {
            var log = _disk.GetFileLength(FileOrigin.Log);
            if (log <= _settings.WalLimit) return;
            // Only a WAL a checkpoint can drain may pass the limit: its log syncs, and its data file too.
            var logSyncs = this.LogSyncs(state, "A log sync");
            if (logSyncs && (_disk.DataSyncConfirmed || this.DataFileSyncs(state, "A data sync"))) return;
            var file = logSyncs ? "data file" : "log file";
            throw new IOException(
                $"Cannot modify this database now: its log file ({Size(log)}) passed the WAL limit ({Size(_settings.WalLimit)}) " +
                $"while the {file} cannot sync to the device (#2242), so no checkpoint can move the log into the data file. " +
                $"Reads keep working, and writes resume once the {file} syncs. The limit is set with \"wal limit\".");
        }

        private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";

        /// <summary>
        /// Sync the log for the WAL limit while its latest barrier answered "cannot sync" (#2242): false
        /// while it still does. A sync that fails (an I/O error) is a write failure, as in
        /// <see cref="DataFileSyncs"/>.
        /// </summary>
        private bool LogSyncs(EngineState state, string operation)
        {
            try { return _disk.LogSyncs(operation); }
            catch (IOException ex)
            {
                // The helper recorded a real failure before it released the WAL writer (#3052), or found
                // one another operation published; the stop follows here, outside its lock.
                state.StopAfter(operation, WriteFailure.InFile(ex, FileOrigin.Log));
                throw;
            }
        }

        /// <summary>
        /// Sync the data file for an operation of the caller's that is not a checkpoint (the WAL limit, a
        /// rebuild): false when it answers "cannot sync" (#2242). A sync that fails (an I/O error) is a
        /// write failure (decision 6): the helper records it before it releases its lock (#3052), the
        /// engine stops here (its next call reopens it read-only), and the operation that tried it throws it.
        /// </summary>
        private bool DataFileSyncs(EngineState state, string operation = "A data sync")
        {
            try { return _disk.DataFileSyncs(operation); }
            catch (IOException ex)
            {
                state.StopAfter(operation, WriteFailure.InFile(ex, FileOrigin.Data));
                throw;
            }
        }
    }
}
