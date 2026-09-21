using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Runs one rebuild with a fixed set of injected install/rollback faults inside an isolated
    /// directory and captures everything the rollback contract makes a promise about.
    /// </summary>
    internal sealed class RebuildFaultRun : IDisposable
    {
        public const string OldValue = "old";

        private readonly string _directory;

        public RebuildFaultRun(RebuildFaultScenario scenario, IReadOnlyCollection<string> faults)
        {
            this.Scenario = scenario;
            this.Faults = faults;
            _directory = Path.Combine(Path.GetTempPath(), "litedb-rebuild-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            Directory.CreateDirectory(_directory);

            this.Live = Path.Combine(_directory, "data.db");
            this.LiveLog = FileHelper.GetLogFile(this.Live);
            this.Backup = FileHelper.GetSuffixFile(this.Live, "-backup", false);
            this.BackupLog = FileHelper.GetSuffixFile(this.LiveLog, "-backup", false);
            this.Temp = FileHelper.GetSuffixFile(this.Live, "-temp", false);
        }

        public RebuildFaultScenario Scenario { get; }
        public IReadOnlyCollection<string> Faults { get; }
        public string Live { get; }
        public string LiveLog { get; }
        public string Backup { get; }
        public string BackupLog { get; }
        public string Temp { get; }

        /// <summary>Every hook reached, in order.</summary>
        public List<string> Hit { get; } = new List<string>();

        /// <summary>Every hook that threw, in order. The first one is the install failure.</summary>
        public List<string> Thrown { get; } = new List<string>();

        public Exception Failure { get; private set; }
        public string LiveState => this.Failure?.Data[RebuildService.LiveStateDataKey] as string;
        public string[] FilesBefore { get; private set; }
        public string[] FilesAfter { get; private set; }

        /// <summary>Result of reading the seeded row through the handle that ran the failed rebuild.</summary>
        public string SameHandleRead { get; private set; }

        /// <summary>Files present after that read: a refused read must not create or alter anything.</summary>
        public string[] FilesAfterRead { get; private set; }

        public IEnumerable<Exception> RollbackErrors =>
            (this.Failure?.Data[RebuildService.RollbackErrorsDataKey] as AggregateException)?.InnerExceptions
            ?? Enumerable.Empty<Exception>();

        public void Execute()
        {
            using (var seed = new LiteDatabase(this.Scenario.OriginalConnection(this.Live)))
            {
                if (this.Scenario.Wal) seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = OldValue });
            }

            this.FilesBefore = this.ListFiles();

            var connection = this.Scenario.OriginalConnection(this.Live);
            connection.Connection = this.Scenario.Connection;

            using (var db = new LiteDatabase(connection))
            {
                RebuildService.SimulateInstallFailure = phase =>
                {
                    this.Hit.Add(phase);
                    if (!this.Faults.Contains(phase)) return;
                    this.Thrown.Add(phase);
                    throw new IOException("injected " + phase);
                };

                try { db.Rebuild(this.Scenario.CreateOptions()); }
                catch (Exception ex) { this.Failure = ex; }
                finally { RebuildService.SimulateInstallFailure = null; }

                this.FilesAfter = this.ListFiles();
                this.SameHandleRead = Read(() => db.GetCollection("rows").FindById(1));
                this.FilesAfterRead = this.ListFiles();
            }
        }

        /// <summary>Open a copy of a data file (plus optional WAL) so inspection never mutates the evidence.</summary>
        public string ReadCopy(string data, string log, bool replacementSettings)
        {
            if (!File.Exists(data)) return "NOFILE";

            var copy = Path.Combine(_directory, "copy-" + Guid.NewGuid().ToString("n").Substring(0, 8) + ".db");
            File.Copy(data, copy);
            if (log != null && File.Exists(log)) File.Copy(log, FileHelper.GetLogFile(copy));

            var connection = replacementSettings
                ? this.Scenario.ReplacementConnection(copy)
                : this.Scenario.OriginalConnection(copy);

            try
            {
                return Read(() =>
                {
                    using (var db = new LiteDatabase(connection))
                    {
                        return db.GetCollection("rows").FindById(1);
                    }
                });
            }
            finally
            {
                File.Delete(copy);
                File.Delete(FileHelper.GetLogFile(copy));
            }
        }

        public string[] ListFiles() => Directory.GetFiles(_directory)
            .Select(Path.GetFileName)
            .Where(name => !name.StartsWith("copy-"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        private static string Read(Func<BsonDocument> read)
        {
            try
            {
                var doc = read();
                return doc == null ? "ROW-MISSING" : doc["value"].AsString;
            }
            catch (Exception ex)
            {
                return "THROWS:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        public override string ToString() =>
            $"[{this.Scenario}] faults=({string.Join(",", this.Thrown)}) state={this.LiveState ?? "success"} " +
            $"files=({string.Join(",", this.FilesAfter ?? new string[0])}) sameHandle={this.SameHandleRead}";

        public void Dispose()
        {
            RebuildService.SimulateInstallFailure = null;
            try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public enum RebuildChange { None, SetPassword, RemovePassword, Collation }

    public sealed class RebuildFaultScenario
    {
        private const string Password = "rebuild-password";

        public RebuildFaultScenario(RebuildChange change, bool wal, ConnectionType connection)
        {
            this.Change = change;
            this.Wal = wal;
            this.Connection = connection;
        }

        public RebuildChange Change { get; }
        public bool Wal { get; }
        public ConnectionType Connection { get; }

        public RebuildOptions CreateOptions()
        {
            switch (this.Change)
            {
                case RebuildChange.SetPassword: return new RebuildOptions { Password = Password };
                case RebuildChange.RemovePassword: return new RebuildOptions { RemovePassword = true };
                case RebuildChange.Collation: return new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") };
                default: return new RebuildOptions();
            }
        }

        public ConnectionString OriginalConnection(string filename) => new ConnectionString
        {
            Filename = filename,
            Password = this.Change == RebuildChange.RemovePassword ? Password : null,
            Collation = new Collation("en-US/None")
        };

        public ConnectionString ReplacementConnection(string filename) => new ConnectionString
        {
            Filename = filename,
            Password = this.Change == RebuildChange.SetPassword ? Password
                : this.Change == RebuildChange.RemovePassword ? null
                : this.OriginalConnection(filename).Password,
            Collation = this.Change == RebuildChange.Collation ? new Collation("en-US/IgnoreCase") : new Collation("en-US/None")
        };

        public override string ToString() => $"{this.Change}/{(this.Wal ? "wal" : "nowal")}/{this.Connection}";
    }
}
