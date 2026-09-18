using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// Holds rebuild output and defers backup cleanup until the replacement database is validated.
    /// </summary>
    internal sealed class RebuildResult
    {
        private readonly string _backupFilename;
        private readonly string _backupLogFilename;
        private readonly bool _deleteBackup;

        public RebuildResult(
            long difference,
            string backupFilename,
            string backupLogFilename,
            bool deleteBackup)
        {
            this.Difference = difference;
            _backupFilename = backupFilename;
            _backupLogFilename = backupLogFilename;
            _deleteBackup = deleteBackup;
        }

        public long Difference { get; }

        /// <summary>
        /// Complete a validated rebuild by deleting backups when retention was disabled.
        /// </summary>
        public void Complete()
        {
            if (!_deleteBackup) return;

            File.Delete(_backupLogFilename);
            File.Delete(_backupFilename);
        }
    }
}
