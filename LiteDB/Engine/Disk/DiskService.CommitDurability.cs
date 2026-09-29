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
        /// that the log file and its directory sync: once per path per process (<see cref="DurableLogs"/>),
        /// so storage that syncs pays it once. The data file need not sync: the WAL starts with a copy
        /// of the data header (decision 11, <see cref="HeaderFrame"/>). A failure is recorded (decision
        /// 6): the engine reopens read-only and refuses writes until the database is reopened.
        /// Caller holds the log writer lock, before the batch writes anything.
        /// </summary>
        private void RequireDurableCommit(Stream stream)
        {
            if (!_durableCommits || _volatileLog || _readOnly || _commitsProven) return;
            try
            {
                this.DataBarrierBeforeFirstCommit();
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
                error.Data[CommitOutcomeDataKey] = NotCommittedOutcome;
                this.RecordWriteFailure("A commit", error);
                throw;
            }
        }

        /// <summary>
        /// Exception.Data key on a failed commit or WAL batch: <see cref="NotCommittedOutcome"/> when its
        /// confirmation cannot be in the log (none written, or its failed append truncated away and the
        /// truncation synced), <see cref="UnknownOutcome"/> when it may be: recovery may then show the
        /// commit after a reopen, so an application that retries needs idempotent writes, or checks after
        /// reopening (decisions 13 and 14).
        /// </summary>
        internal const string CommitOutcomeDataKey = "LiteDB.CommitOutcome";
        internal const string NotCommittedOutcome = "NotCommitted";
        internal const string UnknownOutcome = "Unknown";

        /// <summary>
        /// Decision 14: the header frame covers what the WAL depends on, not what it builds on. A WAL
        /// holds changed pages only, so before an engine's first durable commit sync the data file
        /// once: a database file someone copied into place (a restore, a deployment) is then on the
        /// device before commits build on it. Best effort, never a refusal: a data file that answers
        /// "cannot sync" proceeds, and a real I/O error propagates as the commit's recorded failure.
        /// Once per process and data header (<see cref="ProveDataFile"/>); a shared connection runs it
        /// once, and not at all once it found that the data file cannot sync, so its operations pay
        /// nothing for it.
        /// </summary>
        private void DataBarrierBeforeFirstCommit()
        {
            if (!_dataIsFile || _dataSyncProven) return;
            var shared = _sharedDurability;
            if (shared != null && (shared.DataBarrierDone || shared.FileSyncUnsupported)) return;
#if DEBUG || TESTING
            DataBarrierRan?.Invoke(_dataPath);
#endif
            this.ProveDataFile();
            if (shared != null) shared.DataBarrierDone = true;
        }

#if DEBUG || TESTING
        /// <summary>Test hook: the data barrier before a first commit ran for this data file.</summary>
        internal static Action<string> DataBarrierRan;
#endif

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

        private static string LogDurablePath(EngineSettings settings)
        {
            if (settings.LogStream != null) return (settings.LogStream as FileStream)?.Name is string name && Path.IsPathRooted(name) ? Path.GetFullPath(name) : null;
            if (settings.DataStream != null || string.IsNullOrEmpty(settings.Filename) || settings.Filename == ":memory:" || settings.Filename == ":temp:") return null;
            try { return Path.GetFullPath(FileHelper.GetLogFile(settings.Filename)); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }
    }
}
