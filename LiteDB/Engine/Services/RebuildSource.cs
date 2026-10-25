using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Keeps Direct source handles exclusive through build and installation.
    /// File readers borrow these handles; disposing a reader never releases ownership.
    /// Windows permits renaming through FileShare.Delete; Unix can rename locked inodes.
    /// </summary>
    internal sealed class RebuildSource : IDisposable
    {
        private FileStream _data;
        private FileStream _log;
        private FileStream _candidate;
        private MemoryStream _emptyLog;
        private int _disposed;
        internal EngineSettings Settings { get; }
        internal Exception Failure { get; set; }
        internal bool ReplacementPublished { get; set; }

        internal RebuildSource(EngineSettings settings)
        {
            Settings = settings;
            // Shared/Coordinated callers already exclude operations with their mutex
            // and reader admission. Idle peer handles deliberately allow replacement.
            if (settings.SharedReaderVersions != null) return;
            if (settings.DataStream != null || settings.LogStream != null)
                throw new NotSupportedException("File rebuild requires engine-owned data and WAL streams.");

            Settings = settings.Clone();
            try
            {
                _data = OpenExclusive(settings.Filename);
                var log = FileHelper.GetLogFile(settings.Filename);
                if (FileHelper.ExistsOrThrow(log)) _log = OpenExclusive(log);
                Settings.DataStream = _data;
                // A missing WAL stays missing; never reopen a pathname after taking the claim.
                Settings.LogStream = _log ?? (Stream)(_emptyLog = new MemoryStream());
            }
            catch (Exception ex) { Failure = ex; Dispose(); throw; }
        }

        internal void ClaimReplacement(string filename)
        {
            if (_data != null) _candidate = OpenExclusive(filename);
        }

        private static FileStream OpenExclusive(string filename)
        {
            var share = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? FileShare.Delete : FileShare.None;
            var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, share);
            try
            {
                // FileStream treats unsupported Unix locks as best effort. A rebuild
                // must fail before scanning rather than assume an ineffective claim.
                try
                {
                    using var probe = new FileStream(filename, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException ex) when (ex.IsLocked() ||
                    (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && ex.HResult == 35)) // EWOULDBLOCK on macOS.
                { return stream; }
                throw new PlatformNotSupportedException("Database rebuild requires effective exclusive file-sharing locks: " + filename);
            }
            catch { stream.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var errors = new List<Exception>();
            Close(_candidate, "candidate", errors);
            Close(_log, "log", errors);
            Close(_data, "data", errors);
            _emptyLog?.Dispose();
            if (errors.Count == 0) return;
            var failure = new AggregateException("Failed to release rebuild file ownership.", errors);
            if (Failure != null)
            {
                var prior = Failure.Data[RebuildService.RollbackErrorsDataKey] as Exception;
                if (prior is AggregateException aggregate) errors.InsertRange(0, aggregate.Flatten().InnerExceptions);
                else if (prior != null) errors.Insert(0, prior);
                Failure.Data[RebuildService.RollbackErrorsDataKey] = new AggregateException(errors);
                return;
            }
            if (ReplacementPublished) failure.Data[RebuildService.LiveStateDataKey] = RebuildService.LiveStateReplacement;
            throw failure;
        }

        private static void Close(Stream stream, string kind, ICollection<Exception> errors)
        {
            if (stream == null) return;
            try
            {
                stream.Dispose();
#if DEBUG || TESTING
                RebuildService.SimulateOwnershipFailure?.Invoke("after-rebuild-" + kind + "-close");
#endif
            }
            catch (Exception ex) { errors.Add(ex); }
        }
    }
}
