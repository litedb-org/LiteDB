using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A real file-backed database whose committed tail is still only in the WAL and
    /// whose data file (and, for a legacy v8 image, WAL) ends with an incomplete page:
    /// the first bytes of a copy of the last complete page, as a torn extension leaves it.
    /// </summary>
    internal sealed class StartupTailTrimFixture : IDisposable
    {
        private const string Crash = "Simulated process death at ";

        private readonly TempFile _file = new TempFile();
        private readonly bool _legacy;
        private readonly string _password;
        private readonly long _alignedData;

        internal StartupTailTrimFixture(bool legacy, string password)
        {
            _legacy = legacy;
            _password = password;
            using (var db = this.Open()) CommittedRows.Write(db);
            new FileInfo(this.LogFile).Length.Should().BeGreaterThan(0, "committed transactions must remain only in the WAL");
            if (legacy) this.MakeLegacy();
            _alignedData = new FileInfo(this.DataFile).Length;
            AppendTornPage(this.DataFile, 5003);
            if (legacy) AppendTornPage(this.LogFile, 3001);
        }

        internal string DataFile => _file.Filename;

        internal string LogFile => FileHelper.GetLogFile(_file.Filename);

        internal LiteDatabase Open() => new LiteDatabase(new ConnectionString
        {
            Filename = this.DataFile,
            Password = _password,
            CompactStorage = _legacy ? CompactStorageMode.Legacy : CompactStorageMode.Auto
        });

        /// <summary>
        /// Opens with the process dying at the <paramref name="occurrence"/>-th hit of
        /// <paramref name="boundary"/>. Returns false when the open never reached it.
        /// </summary>
        internal bool OpenInterruptedAt(string boundary, int occurrence, bool processDeath)
        {
            var owner = Environment.CurrentManagedThreadId;
            var hits = 0;
            byte[] data = null;
            byte[] log = null;
            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase != boundary || Environment.CurrentManagedThreadId != owner || ++hits != occurrence) return;
                // What survives process death: the bytes the OS holds at this instant.
                data = TempFile.ReadAllBytesShared(this.DataFile);
                log = File.Exists(this.LogFile) ? TempFile.ReadAllBytesShared(this.LogFile) : null;
                throw new IOException(Crash + phase);
            };
            Exception failure;
            try { failure = Record.Exception(() => { using var db = this.Open(); }); }
            finally { EngineState.SimulateProcessCrash = null; }

            if (data == null)
            {
                failure.Should().BeNull("an open that does not reach {0} must succeed", boundary);
                return false;
            }
            failure.Should().BeOfType<IOException>().Which.Message.Should().Be(Crash + boundary);
            if (processDeath)
            {
                // A dead process runs no error-close cleanup: put the captured image back.
                File.WriteAllBytes(this.DataFile, data);
                if (log != null) File.WriteAllBytes(this.LogFile, log);
                else if (File.Exists(this.LogFile)) File.Delete(this.LogFile);
            }
            return true;
        }

        /// <summary>Opens without interruption and returns the trim boundaries it passed.</summary>
        internal List<string> OpenRecordingTrims(bool withExtra)
        {
            var owner = Environment.CurrentManagedThreadId;
            var trims = new List<string>();
            EngineState.SimulateProcessCrash = phase =>
            {
                if (Environment.CurrentManagedThreadId == owner && phase.StartsWith("startup-", StringComparison.Ordinal))
                    trims.Add(phase);
            };
            try
            {
                using var db = this.Open();
                this.AssertAligned();
                CommittedRows.Verify(db, withExtra);
            }
            finally { EngineState.SimulateProcessCrash = null; }
            return trims;
        }

        /// <summary>
        /// Opens normally, checks every committed row and index result, writes and
        /// checkpoints once more, then proves a further open has no tail left to cut.
        /// </summary>
        internal void VerifyCleanOpen()
        {
            using (var db = this.Open())
            {
                this.AssertAligned();
                var length = new FileInfo(this.DataFile).Length;
                // A legacy open also converts the file, which backfills the WAL first.
                if (_legacy) length.Should().BeGreaterOrEqualTo(_alignedData);
                else length.Should().Be(_alignedData, "only the incomplete trailing page may be cut");
                CommittedRows.Verify(db, false);
                db.GetCollection(CommittedRows.Collection).Insert(CommittedRows.Extra);
                db.Checkpoint();
            }
            this.AssertAligned();
            this.OpenRecordingTrims(true).Should().BeEmpty("recovery completed: no incomplete tail may remain");
        }

        private void AssertAligned()
        {
            (new FileInfo(this.DataFile).Length % PAGE_SIZE).Should().Be(0, "the data file must end on a page boundary");
            if (_legacy && File.Exists(this.LogFile))
                (new FileInfo(this.LogFile).Length % PAGE_SIZE).Should().Be(0, "the WAL must end on a page boundary");
        }

        private void MakeLegacy()
        {
            using var data = ChecksumTestFiles.Copy(File.ReadAllBytes(this.DataFile));
            using var log = ChecksumTestFiles.Copy(File.ReadAllBytes(this.LogFile));
            ChecksumTestFiles.MakeLegacy(data, log, _password);
            File.WriteAllBytes(this.DataFile, data.ToArray());
            File.WriteAllBytes(this.LogFile, log.ToArray());
        }

        private static void AppendTornPage(string filename, int length)
        {
            var bytes = File.ReadAllBytes(filename);
            (bytes.Length % PAGE_SIZE).Should().Be(0, "the fixture starts from complete pages");
            using var stream = new FileStream(filename, FileMode.Append, FileAccess.Write);
            stream.Write(bytes, bytes.Length - PAGE_SIZE, length);
            stream.Flush(true);
        }

        public void Dispose()
        {
            EngineState.SimulateProcessCrash = null;
            if (File.Exists(this.LogFile)) File.Delete(this.LogFile);
            _file.Dispose();
        }
    }
}
