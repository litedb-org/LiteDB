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

        private static readonly Dictionary<string, KeyValuePair<byte[], byte[]>> Seeds =
            new Dictionary<string, KeyValuePair<byte[], byte[]>>();

        private readonly string _directory;
        private int _hitsAtLastFault;

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
            this.Marker = RebuildRecovery.GetMarkerFilename(this.Live);
        }

        public RebuildFaultScenario Scenario { get; }
        public IReadOnlyCollection<string> Faults { get; }
        public string Live { get; }
        public string LiveLog { get; }
        public string Backup { get; }
        public string BackupLog { get; }
        public string Temp { get; }
        public string Marker { get; }

        /// <summary>Every hook reached, in order.</summary>
        public List<string> Hit { get; } = new List<string>();

        /// <summary>Every hook that threw, in order. The first one is the install failure.</summary>
        public List<string> Thrown { get; } = new List<string>();

        /// <summary>
        /// Hooks reached after the last fault. Only these can extend this fault sequence: failing an
        /// earlier hook instead diverts execution before the later faults are ever reached.
        /// </summary>
        public IEnumerable<string> ReachedAfterLastFault => this.Hit.Skip(_hitsAtLastFault);

        public Exception Failure { get; private set; }
        public string LiveState => this.Failure?.Data[RebuildService.LiveStateDataKey] as string;
        public string[] FilesBefore { get; private set; }
        public string[] FilesAfter { get; private set; }

        /// <summary>The recovery marker was left behind: every open of the live path must be refused.</summary>
        public bool Blocked => this.FilesAfter.Contains(Path.GetFileName(this.Marker));

        /// <summary>A WAL was at the live path when the rebuild returned, before any later use.</summary>
        public bool HadLiveLog => this.FilesAfter.Contains(Path.GetFileName(this.LiveLog));

        /// <summary>Result of reading the seeded rows through the handle that ran the rebuild.</summary>
        public string SameHandleRead { get; private set; }

        /// <summary>Result of inserting through that handle, taken after the read.</summary>
        public string SameHandleWrite { get; private set; }

        /// <summary>Files present after a refused read and write: neither may create or alter anything.</summary>
        public string[] FilesAfterRefusedUse { get; private set; }

        public IEnumerable<Exception> RollbackErrors =>
            (this.Failure?.Data[RebuildService.RollbackErrorsDataKey] as AggregateException)?.InnerExceptions
            ?? Enumerable.Empty<Exception>();

        public void Execute()
        {
            this.Seed();

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
                    _hitsAtLastFault = this.Hit.Count;
                    throw new IOException("injected " + phase);
                };

                try { db.Rebuild(this.Scenario.CreateOptions()); }
                catch (Exception ex) { this.Failure = ex; }
                finally { RebuildService.SimulateInstallFailure = null; }

                this.FilesAfter = this.ListFiles();
                this.SameHandleRead = Read(db);
                if (!this.Blocked) return;

                this.SameHandleWrite = Attempt(() => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 }));
                this.FilesAfterRefusedUse = this.ListFiles();
            }
        }

        /// <summary>
        /// One checkpointed row and one acknowledged row that, with a WAL, exists only there.
        /// Seeded once per scenario: the matrix starts hundreds of runs from the same files.
        /// </summary>
        private void Seed()
        {
            KeyValuePair<byte[], byte[]> files;
            lock (Seeds)
            {
                if (!Seeds.TryGetValue(this.Scenario.ToString(), out files))
                {
                    using (var seed = new LiteDatabase(this.Scenario.OriginalConnection(this.Live)))
                    {
                        seed.CheckpointSize = 0;
                        seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed" });
                        seed.Checkpoint();
                        seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "acknowledged" });
                        if (!this.Scenario.Wal) seed.Checkpoint();
                    }

                    files = new KeyValuePair<byte[], byte[]>(
                        File.ReadAllBytes(this.Live),
                        File.Exists(this.LiveLog) ? File.ReadAllBytes(this.LiveLog) : null);
                    Seeds[this.Scenario.ToString()] = files;
                    return;
                }
            }

            File.WriteAllBytes(this.Live, files.Key);
            if (files.Value != null) File.WriteAllBytes(this.LiveLog, files.Value);
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
                return OpenAndRead(connection);
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

        /// <summary>Open the live path itself with a fresh handle. Only meaningful when it must be refused.</summary>
        public string OpenLive(ConnectionType connection, bool replacementSettings)
        {
            var settings = replacementSettings
                ? this.Scenario.ReplacementConnection(this.Live)
                : this.Scenario.OriginalConnection(this.Live);
            settings.Connection = connection;

            return OpenAndRead(settings);
        }

        private static string OpenAndRead(ConnectionString connection) => Attempt(() =>
        {
            using (var db = new LiteDatabase(connection))
            {
                return Read(db);
            }
        });

        /// <summary>"old" only when the checkpointed row and the WAL-only row are both intact.</summary>
        private static string Read(LiteDatabase db) => Attempt(() =>
        {
            var rows = db.GetCollection("rows");
            var complete = rows.Count() == 2 &&
                rows.FindById(1)?["value"].AsString == "checkpointed" &&
                rows.FindById(2)?["value"].AsString == "acknowledged";

            return complete ? OldValue : "ROWS-MISSING";
        });

        private static string Attempt(Func<object> action)
        {
            try
            {
                return action() as string ?? "OK";
            }
            catch (Exception ex)
            {
                var code = ex is LiteException lite ? "#" + lite.ErrorCode : "";
                return "THROWS:" + ex.GetType().Name + code + ":" + ex.Message;
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
