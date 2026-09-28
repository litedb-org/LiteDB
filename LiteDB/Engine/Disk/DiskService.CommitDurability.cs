using System;
using System.IO;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // Set once this engine proved what a durable commit needs (RequireDurableCommit); cleared when
        // the log or its directory answers "cannot sync" later.
        private volatile bool _commitsProven;

        /// <summary>
        /// Decision 3 of docs/decisions/durability-policy.md: with durable commits a commit that cannot
        /// be made durable throws, before it writes a frame. Before this engine's first WAL batch, prove
        /// that the data header the WAL's frames depend on is on the device (<see cref="DataHeaderDurable"/>)
        /// and that the log file and its directory sync: the log once per path per process
        /// (<see cref="DurableLogs"/>), so storage that syncs pays it once. A failure is recorded (decision
        /// 6): the engine reopens read-only and refuses writes until the database is reopened.
        /// Caller holds the log writer lock, before the batch writes anything.
        /// </summary>
        private void RequireDurableCommit(Stream stream)
        {
            if (!_durableCommits || _volatileLog || _readOnly || _commitsProven) return;
            try
            {
                if (!this.DataHeaderDurable())
                {
                    throw UnsyncedStorage(WriteFailure.InFile(new IOException(NotWritten +
                        "the data file cannot sync to the device (#2242) and its header is not known to be on the device, " +
                        "so the log file cannot make a commit durable." + OptOut), FileOrigin.Data));
                }
                if (_logPath == null || !DurableLogs.Contains(_logPath))
                {
                    var raw = stream is ChecksummedWalStream wal ? wal.RawStream : stream;
                    try
                    {
                        raw.FlushToDisk();
                        _logBarrierSynced = true;
                    }
                    catch (Exception ex) when (IsDurableFlushUnsupported(ex))
                    {
                        _logBarrierSynced = false;
                        this.MarkLogFlushDegraded(ex);
                        throw LogCannotSync(ex, "the log file", NotWritten);
                    }
                    catch (Exception ex) when (FailedIn(ex, FileOrigin.Log))
                    {
                    }
                    this.SyncLogDirectory();
                    // Without a C library no sync can report failure (LogSyncUnverified): attempted, not a failure.
                    if (_logDirectoryUnsyncable && !this.LogSyncUnverified) throw LogCannotSync(null, "the log file's directory", NotWritten);
                    if (_logPath != null) DurableLogs.Record(_logPath);
                }
                _commitsProven = true;
            }
            catch (IOException error)
            {
                this.RecordWriteFailure("A commit", error);
                throw;
            }
        }

        private const string NotWritten = "This commit was not written: ";
        private const string OutcomeUnknown = "This commit's outcome is unknown: its frames reached the operating system, but ";
        private const string OptOut = " Open the database with \"durable commits=false\" to accept commits that a power loss may lose.";

        /// <summary>
        /// The log (or its directory) answered "cannot sync" (#2242) while commits must be durable: no
        /// commit is acknowledged there (decision 3). <paramref name="context"/> says what became of the
        /// commit that found out: <see cref="NotWritten"/>, <see cref="OutcomeUnknown"/>, or nothing for a
        /// recovery barrier (a checkpoint's journal, a conversion, a repair), which stops before it writes on.
        /// </summary>
        private static IOException LogCannotSync(Exception cause, string part, string context = null) =>
            UnsyncedStorage(WriteFailure.InFile(new IOException(
                (context ?? $"The {part.Substring(4)} cannot sync to the device (#2242): ") +
                (context == null ? "no commit can be made durable there." : $"{part} cannot sync to the device (#2242), so no commit can be made durable there.") +
                OptOut, cause), FileOrigin.Log));

        /// <summary>
        /// The data header the WAL's frames depend on (its salt, version and creation time) is on the
        /// device: this engine synced it, or it is the header a successful sync in this process left
        /// (<see cref="ProveDataFile"/>). Only then can a log sync make a commit durable, also while the
        /// data file cannot sync (decision 4): every data write after the header's sync is covered by
        /// the WAL, which shrinks only behind a covering data sync (<see cref="ShrinkLog"/>). A data
        /// stream that is not a file cannot answer "cannot sync" and needs no proof.
        /// </summary>
        private bool DataHeaderDurable()
        {
            if (_readOnly || !_dataIsFile) return true;
            if (!_dataSyncProven) this.ProveDataFile();
            return _dataSyncProven;
        }

        private static string LogDurablePath(EngineSettings settings)
        {
            if (settings.LogStream != null) return (settings.LogStream as FileStream)?.Name is string name && Path.IsPathRooted(name) ? Path.GetFullPath(name) : null;
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) || settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;
            try { return Path.GetFullPath(FileHelper.GetLogFile(settings.Filename)); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }
    }
}
