using System;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Record a write or sync failure before the stop it causes. walKept counts whatever the log
        /// file holds, not only the WAL's frames: an outstanding header journal is kept like the WAL.
        /// </summary>
        internal void RecordWriteFailure(string operation, Exception error)
        {
            bool walKept;
            try { walKept = this.LogHoldsAnything(); }
            catch (Exception) { walKept = true; }
            _state.RecordWriteFailure(new WriteFailure(operation, error, walKept));
        }

        /// <summary>
        /// The log file holds something recovery may need: WAL frames, an outstanding header journal, a
        /// legacy header backup (a conversion's, after its drain emptied the WAL).
        /// </summary>
        private bool LogHoldsAnything()
        {
            var raw = _writer.IsValueCreated ? (_writer.Value as ChecksummedWalStream)?.RawStream ?? _writer.Value : null;
            return this.GetFileLength(FileOrigin.Log) > 0 || _checksums.JournalBytes != 0 || (raw?.Length ?? 0) > 0;
        }

        /// <summary>
        /// For an exception filter around a WAL batch write: the failure is the log file's, unless a
        /// deeper failure named another. An engine that already stopped throws its stop error, which
        /// names nothing. Lets the failure pass.
        /// </summary>
        internal bool NameLogWriteFailure(Exception error)
        {
            if (!_state.Stopped) WriteFailure.InFile(error, FileOrigin.Log);
            return false;
        }

        /// <summary>See <see cref="EngineState.RequireNoWriteFailure"/>.</summary>
        internal void RequireNoWriteFailure() => _state.RequireNoWriteFailure();
    }
}
