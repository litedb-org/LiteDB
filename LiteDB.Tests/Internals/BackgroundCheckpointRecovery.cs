using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;

namespace LiteDB.Internals
{
    internal static class BackgroundCheckpointRecovery
    {
        internal static void Verify(string filename, string password, int version)
        {
            var data = File.ReadAllBytes(filename);
            var wal = FileHelper.GetLogFile(filename);
            var log = File.Exists(wal) ? File.ReadAllBytes(wal) : Array.Empty<byte>();
            try { VerifyCycles(filename, password, version); }
            catch (Exception error) { throw Preserve(data, log, error); }
        }

        private static void VerifyCycles(string filename, string password, int version)
        {
            var log = FileHelper.GetLogFile(filename);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var beforeData = File.ReadAllBytes(filename);
                var beforeLog = File.Exists(log) ? File.ReadAllBytes(log) : null;
                using (var read = new LiteDatabase(new ConnectionString { Filename = filename, Password = password, ReadOnly = true }))
                    BackgroundCheckpointWorkload.Verify(read, version);
                File.ReadAllBytes(filename).Should().Equal(beforeData);
                if (beforeLog == null) File.Exists(log).Should().BeFalse();
                else File.ReadAllBytes(log).Should().Equal(beforeLog);
                using (var engine = new LiteEngine(new EngineSettings { Filename = filename, Password = password }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    BackgroundCheckpointWorkload.Verify(db, version);
                    db.Checkpoint();
                }
                AllocationReachability.Verify(filename, password);
            }
        }

        internal static void Verify(byte[] data, byte[] log, string password, int version)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(file.Filename), log);
            Verify(file.Filename, password, version);
        }

        internal static Exception Preserve(byte[] data, byte[] log, Exception error)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-background-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "failure.db"), data);
            File.WriteAllBytes(Path.Combine(directory, "failure-log.db"), log);
            return new InvalidOperationException("Background crash images preserved at " + directory, error);
        }
    }
}
