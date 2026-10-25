using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// [ThreadSafe]
    /// </summary>
    internal class RebuildService
    {
        /// <summary>
        /// Exception.Data key on a failed installation: which database the rollback left at the live path.
        /// </summary>
        internal const string LiveStateDataKey = "LiteDB.Rebuild.LiveState";
        internal const string RollbackErrorsDataKey = "LiteDB.Rebuild.RollbackErrors";

        /// <summary>The original data file and WAL are back at their live paths.</summary>
        internal const string LiveStateOriginal = "original-restored";

        /// <summary>The completed replacement is live; the original pair stays at the backup paths.</summary>
        internal const string LiveStateReplacement = "replacement-published";

        /// <summary>
        /// Two or more rollback steps failed. The live path is missing or holds the original data
        /// without its WAL; the complete copies are in the -backup and -temp files and the
        /// recovery marker keeps the database from being opened.
        /// </summary>
        internal const string LiveStateIncomplete = "incomplete";

        private const int ReplacementDeleteTimeoutSeconds = 5;

#if DEBUG || TESTING
        internal static Action<string> SimulateInstallFailure;
#endif
        private readonly EngineSettings _settings;
        private readonly int _fileVersion;

        public RebuildService(EngineSettings settings)
        {
            _settings = settings;
            RebuildRecovery.EnsureAvailable(settings);

            // test for prior version
            var bufferV7 = this.ReadFirstBytes(false);
            if (FileReaderV7.IsVersion(bufferV7))
            {
                _fileVersion = 7;
                return;
            }

            // open, read first 16kb, and close data file
            var buffer = this.ReadFirstBytes();

            // test for valid reader to use
            _fileVersion = FileReaderV8.IsVersion(buffer) ?
                buffer[HeaderPage.P_FILE_VERSION] : throw LiteException.InvalidDatabase();
        }

        public long Rebuild(RebuildOptions options, Collation currentCollation = null)
        {
            var backupFilename = FileHelper.GetSuffixFile(_settings.Filename, "-backup", true);
            var backupLogFilename = FileHelper.GetSuffixFile(FileHelper.GetLogFile(_settings.Filename), "-backup", true);
            var tempFilename = FileHelper.GetSuffixFile(_settings.Filename, "-temp", true);

            try
            {
                this.BuildReplacement(tempFilename, options, currentCollation);
            }
            catch (Exception buildException)
            {
                DiscardReplacement(tempFilename, buildException);
                throw;
            }

            return this.Install(backupFilename, backupLogFilename, tempFilename);
        }

        /// <summary>
        /// Read everything the file reader can recover into a new, checkpointed database file.
        /// </summary>
        private void BuildReplacement(string tempFilename, RebuildOptions options, Collation currentCollation)
        {
            // open file reader
            var compactStorage = options.CompactStorage ?? _settings.CompactStorage;

            using (var reader = _fileVersion == 7 ?
                new FileReaderV7(_settings) :
                (IFileReader)new FileReaderV8(_settings, options.Errors))
            {
                // open file reader and ready to import to new temp engine instance
                reader.Open();

                // open new engine to recive all data readed from FileReader
                using (var engine = new LiteEngine(new EngineSettings
                {
                    Filename = tempFilename,
                    CompactStorage = compactStorage,
                    Collation = options.Collation ?? currentCollation,
                    Password = options.ResolvePassword(_settings.Password),
                }))
                {
                    // copy all database to new Log file with NO checkpoint during all rebuild
                    engine.Pragma(Pragmas.CHECKPOINT, 0);

                    // rebuild all content from reader into new engine
                    engine.RebuildContent(reader);

                    // insert error report
                    if (options.IncludeErrorReport && options.Errors.Count > 0)
                    {
                        var report = options.GetErrorReport();

                        engine.Insert("_rebuild_errors", report, BsonAutoId.Int32);
                    }

                    // update pragmas
                    var pragmas = reader.GetPragmas();

                    engine.Pragma(Pragmas.CHECKPOINT, pragmas[Pragmas.CHECKPOINT]);
                    engine.Pragma(Pragmas.TIMEOUT, pragmas[Pragmas.TIMEOUT]);
                    engine.Pragma(Pragmas.LIMIT_SIZE, pragmas[Pragmas.LIMIT_SIZE]);
                    engine.Pragma(Pragmas.UTC_DATE, pragmas[Pragmas.UTC_DATE]);
                    engine.Pragma(Pragmas.USER_VERSION, pragmas[Pragmas.USER_VERSION]);

                    // after rebuild, copy log bytes into data file
                    engine.Checkpoint();
                }
            }

        }

        /// <summary>
        /// Publish the completed replacement at the live path, keeping the original data file and
        /// WAL as backups. On failure roll back to a complete database or leave access guarded.
        /// </summary>
        internal long Install(string backupFilename, string backupLogFilename, string tempFilename)
        {
            // Read metadata before installation, so no fallible work separates the
            // completed replacement from the caller updating its engine settings.
            var difference = new FileInfo(_settings.Filename).Length - new FileInfo(tempFilename).Length;

            // if log file exists, rename as backup file
            var logFile = FileHelper.GetLogFile(_settings.Filename);

            var movedLog = false;
            var movedSource = false;
            var candidateIsLive = false;
            // Persist the guard before the first rename. Recovery must not depend
            // on being able to write a marker after filesystem operations fail.
            try
            {
                RebuildRecovery.Begin(_settings.Filename, backupFilename, backupLogFilename, tempFilename);
            }
            catch (Exception markerException)
            {
                // No database file has moved, so the replacement will never be published.
                DiscardReplacement(tempFilename, markerException);
                throw;
            }

            try
            {
#if DEBUG || TESTING
                SimulateInstallFailure?.Invoke("before-log-backup");
#endif
                // An original WAL that stays live would be replayed over the replacement, so
                // "could not look" must fail the installation instead of reading as "no WAL".
                if (FileHelper.ExistsOrThrow(logFile))
                {
                    File.Move(logFile, backupLogFilename);
                    movedLog = true;
                }
#if DEBUG || TESTING
                SimulateInstallFailure?.Invoke("after-log-backup");
                SimulateInstallFailure?.Invoke("before-source-backup");
#endif

                // rename source filename to backup name
                FileHelper.Exec(5, () => File.Move(_settings.Filename, backupFilename));
                movedSource = true;
#if DEBUG || TESTING
                SimulateInstallFailure?.Invoke("after-source-backup");
                SimulateInstallFailure?.Invoke("before-temp-install");
#endif

                // rename temp file into filename
                File.Move(tempFilename, _settings.Filename);
                candidateIsLive = true;
#if DEBUG || TESTING
                SimulateInstallFailure?.Invoke("after-temp-install");
#endif
                RebuildRecovery.Complete(_settings.Filename);
            }
            catch (Exception installException)
            {
                var rollbackErrors = new List<Exception>();
                var sourceIsLive = !movedSource;
                var logIsLive = !movedLog;

                TryRollback(() =>
                {
#if DEBUG || TESTING
                    SimulateInstallFailure?.Invoke("before-candidate-rollback");
#endif
                    // Installation may already have placed the replacement at the
                    // live path. Move it back out before restoring the old data/WAL
                    // pair; mixing a new encrypted data file with the old WAL makes
                    // both otherwise-complete states unreadable.
                    if (movedSource && File.Exists(backupFilename) && File.Exists(_settings.Filename))
                    {
                        File.Move(_settings.Filename, tempFilename);
                        candidateIsLive = false;
                    }
                }, rollbackErrors);

                TryRollback(() =>
                {
#if DEBUG || TESTING
                    SimulateInstallFailure?.Invoke("before-source-rollback");
#endif
                    if (movedSource && File.Exists(backupFilename))
                    {
                        File.Move(backupFilename, _settings.Filename);
                        sourceIsLive = true;
                    }
                }, rollbackErrors);

                TryRollback(() =>
                {
                    // The original WAL is only compatible with the original data
                    // file. Keep it at the backup path when data restoration failed.
                    if (!sourceIsLive) return;
#if DEBUG || TESTING
                    SimulateInstallFailure?.Invoke("before-log-rollback");
#endif
                    if (!File.Exists(logFile) && movedLog && File.Exists(backupLogFilename))
                    {
                        File.Move(backupLogFilename, logFile);
                        logIsLive = true;
                    }
                }, rollbackErrors);

                if (sourceIsLive && !logIsLive)
                {
                    TryRollback(() =>
                    {
#if DEBUG || TESTING
                        SimulateInstallFailure?.Invoke("before-source-retraction");
#endif
                        // A source without its WAL can silently omit acknowledged
                        // commits. Retract it so the backup remains a coherent pair.
                        if (File.Exists(_settings.Filename) && !File.Exists(backupFilename))
                        {
                            File.Move(_settings.Filename, backupFilename);
                            sourceIsLive = false;
                        }
                    }, rollbackErrors);
                }

                TryRollback(() =>
                {
                    // Prefer the completed replacement at the live path whenever
                    // restoration of the original data/WAL pair did not finish.
                    if (!sourceIsLive && File.Exists(tempFilename) && !File.Exists(_settings.Filename))
                    {
#if DEBUG || TESTING
                        SimulateInstallFailure?.Invoke("before-candidate-republish");
#endif
                        File.Move(tempFilename, _settings.Filename);
                        candidateIsLive = true;
                    }
                }, rollbackErrors);

                var originalIsLive = sourceIsLive && logIsLive;

                if (originalIsLive)
                {
                    // The unpublished replacement is a full copy of the database, possibly
                    // without the encryption of the original. Do not leave it behind.
                    TryRollback(() =>
                    {
#if DEBUG || TESTING
                        SimulateInstallFailure?.Invoke("before-candidate-cleanup");
#endif
                        DeleteReplacement(tempFilename);
                    }, rollbackErrors);
                }

                // Rollback is bounded: it settles on the original pair, else on the
                // replacement, else it reports what it could not repair. Any of the file
                // operations above can fail, so no further compensation is attempted.
                var liveState =
                    originalIsLive ? LiveStateOriginal :
                    candidateIsLive ? LiveStateReplacement :
                    LiveStateIncomplete;

                installException.Data[LiveStateDataKey] = liveState;

                // An incomplete rollback keeps the recovery marker: neither stale source
                // data nor a missing file may be opened as a database.
                if (liveState != LiveStateIncomplete)
                    TryRollback(() => RebuildRecovery.Complete(_settings.Filename), rollbackErrors);

                if (rollbackErrors.Count > 0)
                    installException.Data[RollbackErrorsDataKey] = new AggregateException(rollbackErrors);
                throw;
            }

            return difference;
        }

        /// <summary>
        /// A replacement that will not be published is a full copy of the database, possibly without
        /// the encryption of the original. While it is built its content is in its WAL as well.
        /// </summary>
        private static void DeleteReplacement(string tempFilename)
        {
            // Wait out a virus scanner or sync client inspecting the new file, as for the marker.
            FileHelper.Exec(ReplacementDeleteTimeoutSeconds, () => File.Delete(tempFilename));
            FileHelper.Exec(ReplacementDeleteTimeoutSeconds, () => File.Delete(FileHelper.GetLogFile(tempFilename)));
        }

        private static void DiscardReplacement(string tempFilename, Exception failure)
        {
            var cleanupErrors = new List<Exception>();
            TryRollback(() => DeleteReplacement(tempFilename), cleanupErrors);
            if (cleanupErrors.Count > 0)
                failure.Data[RollbackErrorsDataKey] = new AggregateException(cleanupErrors);
        }

        private static void TryRollback(Action rollback, ICollection<Exception> errors)
        {
            try { rollback(); }
            catch (Exception error) { errors.Add(error); }
        }

        /// <summary>
        /// Read first 16kb (2 PAGES) in bytes
        /// </summary>
        private byte[] ReadFirstBytes(bool useAesStream = true)
        {
            var buffer = new byte[PAGE_SIZE * 2];
            var factory = _settings.CreateDataFactory(useAesStream);

            using (var stream = factory.GetStream(false, true))
            {
                stream.Position = 0;
                stream.ReadFully(buffer, 0, buffer.Length);
            }

            return buffer;
        }
    }
}
