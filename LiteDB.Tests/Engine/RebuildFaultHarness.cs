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

        // The slow verifications - opening copies, probing the live path, retrying the rebuild -
        // depend only on what a run left on disk. Hundreds of fault sequences end in the same few
        // disk states, so each distinct state is verified once and every run is still executed.
        private static readonly Dictionary<string, string> Verified = new Dictionary<string, string>();

        private readonly string _directory;
        private int _hitsAtLastFault;

        public RebuildFaultRun(RebuildFaultScenario scenario, IReadOnlyCollection<string> faults, bool installOnly = false)
        {
            this.InstallOnly = installOnly;
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

        /// <summary>
        /// Drive only the installation of a replacement built once per scenario. Building it is the
        /// slow, disk-heavy part of a rebuild and is identical for every fault sequence; the install
        /// and its rollback are what the faults exercise. No database handle exists in this mode.
        /// </summary>
        public bool InstallOnly { get; }
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

        /// <summary>
        /// Identifies the disk state a run ended in: the scenario, the reported state, and for every
        /// file its name and whether it still is the original data file or the original WAL.
        /// </summary>
        public string Signature { get; private set; }

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

            if (this.InstallOnly)
            {
                this.ExecuteInstall();
                return;
            }

            var connection = this.Scenario.OriginalConnection(this.Live);
            connection.Connection = this.Scenario.Connection;

            using (var db = new LiteDatabase(connection))
            {
                this.Inject();

                try { db.Rebuild(this.Scenario.CreateOptions()); }
                catch (Exception ex) { this.Failure = ex; }
                finally { RebuildService.SimulateInstallFailure = null; }

                this.Capture();
                this.SameHandleRead = Read(db);
                if (!this.Blocked) return;

                this.SameHandleWrite = Attempt(() => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 }));
                this.FilesAfterRefusedUse = this.ListFiles();
            }
        }

        private void ExecuteInstall()
        {
            File.WriteAllBytes(this.Temp, this.Scenario.Replacement());

            var settings = new EngineSettings
            {
                Filename = this.Live,
                Password = this.Scenario.OriginalConnection(this.Live).Password
            };

            this.Inject();
            try { new RebuildService(settings).Install(this.Backup, this.BackupLog, this.Temp); }
            catch (Exception ex) { this.Failure = ex; }
            finally { RebuildService.SimulateInstallFailure = null; }

            this.Capture();
        }

        private void Inject()
        {
            RebuildService.SimulateInstallFailure = phase =>
            {
                this.Hit.Add(phase);
                if (!this.Faults.Contains(phase)) return;
                this.Thrown.Add(phase);
                _hitsAtLastFault = this.Hit.Count;
                throw new IOException("injected " + phase);
            };
        }

        private void Capture()
        {
            this.FilesAfter = this.ListFiles();
            this.Signature = this.Scenario.Key + "|" + this.LiveState + "|" + string.Join(",", this.FilesAfter.Select(this.Identify));
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
                if (!Seeds.TryGetValue(this.Scenario.Key, out files))
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
                    Seeds[this.Scenario.Key] = files;
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

            var key = $"{this.Signature}|copy|{Path.GetFileName(data)}|{Path.GetFileName(log ?? "")}|{replacementSettings}";
            return Once(key, () => this.ReadCopyCore(data, log, replacementSettings));
        }

        private string ReadCopyCore(string data, string log, bool replacementSettings)
        {

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
            return Once($"{this.Signature}|open|{connection}|{replacementSettings}", () => this.OpenLiveCore(connection, replacementSettings));
        }

        private string OpenLiveCore(ConnectionType connection, bool replacementSettings)
        {
            var settings = replacementSettings
                ? this.Scenario.ReplacementConnection(this.Live)
                : this.Scenario.OriginalConnection(this.Live);
            settings.Connection = connection;

            return OpenAndRead(settings);
        }

        private static string Once(string key, Func<string> verify)
        {
            lock (Verified)
            {
                if (!Verified.TryGetValue(key, out var result)) Verified[key] = result = verify();
                return result;
            }
        }

        public byte[] ReadLive() => this.ReadShared(this.Live);

        // A direct handle keeps the rebuilt file open, so read alongside it.
        private byte[] ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private string Identify(string name)
        {
            var bytes = this.ReadShared(Path.Combine(_directory, name));
            var seed = Seeds[this.Scenario.Key];
            var identity = bytes.SequenceEqual(seed.Key) ? "original-data"
                : seed.Value != null && bytes.SequenceEqual(seed.Value) ? "original-wal"
                : "other";

            return name + "=" + identity;
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
}
