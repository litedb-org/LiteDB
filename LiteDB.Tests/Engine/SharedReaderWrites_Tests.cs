using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
#if NET
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Shared-mode behavior that pre-v13 readers provided by owning the engine for
    /// their lifetime: cheap writes while iterating, an empty WAL once everything
    /// is closed, and reads without write access to the database directory.
    /// </summary>
    public class SharedReaderWrites_Tests : IDisposable
    {
        private readonly OpenReaders _open = new OpenReaders();
        private const int Count = 200;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shared-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string LogFilename => FileHelper.GetLogFile(this.Filename);

        public SharedReaderWrites_Tests() => Directory.CreateDirectory(_directory);

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        private SharedEngine Open(string password = null, bool readOnly = false) =>
            new SharedEngine(new EngineSettings { Filename = this.Filename, Password = password, ReadOnly = readOnly })
            {
                // Counted opens must not depend on how fast the machine runs the loop.
                PinIdleLimit = TimeSpan.FromMinutes(10),
                PinHoldLimit = TimeSpan.FromMinutes(10)
            };

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Updates_while_iterating_reuse_one_engine_and_leave_no_wal(string password)
        {
            using (var engine = this.Open(password))
            using (var db = new LiteDatabase(engine))
            {
                var col = db.GetCollection("docs");
                col.Insert(Enumerable.Range(1, Count).Select(id => Doc(id, 0)));
                var opens = engine.EngineOpens;

                foreach (var doc in col.FindAll())
                {
                    doc["value"] = 1;
                    col.Update(doc);
                }

                // One open for the leased query, one pinned for all writes. Reopening per
                // write would replay a WAL the reader keeps growing: quadratic in the loop.
                (engine.EngineOpens - opens).Should().BeLessOrEqualTo(2);
                File.Exists(this.LogFilename).Should().BeFalse("the pinned engine closes with a full checkpoint after its reader");
            }

            this.CountInDataFileAlone(password, 1).Should().Be(Count);
            this.AssertMutexIsFree(password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Last_reader_disposal_checkpoints_writes_made_by_another_instance(string password)
        {
            using (var writer = this.Open(password))
            using (var readers = this.Open(password))
            {
                writer.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
                using (var reader = readers.Query("docs", new Query()))
                {
                    writer.Update("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 1)));
                    File.Exists(this.LogFilename).Should().BeTrue("the leased reader prevents a full checkpoint");
                    var seen = 0;
                    while (reader.Read())
                    {
                        reader.Current["value"].AsInt32.Should().Be(0);
                        seen++;
                    }
                    seen.Should().Be(Count);
                }

                File.Exists(this.LogFilename).Should().BeFalse("the last reader's disposal runs the full checkpoint the writer could not");
            }

            this.CountInDataFileAlone(password, 1).Should().Be(Count);
        }

        [Fact]
        public void Reader_disposed_on_another_thread_releases_the_pin()
        {
            using var engine = this.Open();
            engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            var reader = _open.Track(engine.Query("docs", new Query()));
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            MvccCheckpoint_Tests.RunThread(reader.Dispose);
            this.AssertMutexIsFree(null);
            engine.Update("docs", new[] { Doc(2, 1) });

            this.AssertMutexIsFree(null);
            engine.Checkpoint();
            File.Exists(this.LogFilename).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_transaction_inside_iteration_balances_the_pin(bool rollback)
        {
            using (var engine = this.Open())
            using (var db = new LiteDatabase(engine))
            {
                var col = db.GetCollection("docs");
                col.Insert(Enumerable.Range(1, Count).Select(id => Doc(id, 0)));
                var first = true;
                foreach (var doc in col.FindAll())
                {
                    if (!first) continue;
                    first = false;
                    col.Update(Doc(doc["_id"].AsInt32, 1));
                    db.BeginTrans().Should().BeTrue();
                    col.Insert(Doc(Count + 1, 2));
                    if (rollback) db.Rollback().Should().BeTrue();
                    else db.Commit().Should().BeTrue();
                }

                col.Count().Should().Be(rollback ? Count : Count + 1);
            }

            this.AssertMutexIsFree(null);
            File.Exists(this.LogFilename).Should().BeFalse();
        }

        [Fact]
        public void Unusable_lease_registry_streams_under_the_mutex()
        {
            using (var engine = this.Open())
            {
                engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            }

            var settings = new EngineSettings
            {
                Filename = this.Filename,
                ReadOnly = true,
                SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied")
            };
            using (var readOnly = new SharedEngine(settings))
            using (var reader = readOnly.Query("docs", new Query()))
            {
                var seen = 0;
                while (reader.Read()) seen++;
                seen.Should().Be(Count);
            }

            Directory.Exists(this.Filename + "-readers").Should().BeFalse();
            this.AssertMutexIsFree(null);
        }

#if NET
        [Fact]
        public void Read_only_connection_reads_a_large_result_in_a_read_only_directory()
        {
            if (!OperatingSystem.IsWindows()) return;
            using (var engine = this.Open())
            {
                engine.Insert("docs", Enumerable.Range(1, Count).Select(id => Doc(id, 0)), BsonAutoId.Int32);
            }
            File.Exists(this.LogFilename).Should().BeFalse();

            var info = new DirectoryInfo(_directory);
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
            var security = info.GetAccessControl();
            security.AddAccessRule(deny);
            info.SetAccessControl(security);
            try
            {
                using var db = new LiteDatabase($"Filename={this.Filename};Connection=shared;ReadOnly=true");
                db.GetCollection("docs").FindAll().Count().Should().Be(Count);
            }
            finally
            {
                security = info.GetAccessControl();
                security.RemoveAccessRule(deny);
                info.SetAccessControl(security);
            }
        }
#endif

        /// <summary>What a user who copies only the closed data file gets.</summary>
        private int CountInDataFileAlone(string password, int value)
        {
            var copy = Path.Combine(_directory, "copy-" + Guid.NewGuid().ToString("N") + ".db");
            File.Copy(this.Filename, copy);
            using var db = new LiteDatabase(new ConnectionString { Filename = copy, Password = password, ReadOnly = true });
            return db.GetCollection("docs").Count(Query.EQ("value", value));
        }

        private void AssertMutexIsFree(string password)
        {
            // Another thread can only write if no mutex recursion was leaked.
            MvccCheckpoint_Tests.RunThread(() =>
            {
                using var other = this.Open(password);
                other.Insert("probe", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
                other.DropCollection("probe");
            });
        }

        public void Dispose()
        {
            _open.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
