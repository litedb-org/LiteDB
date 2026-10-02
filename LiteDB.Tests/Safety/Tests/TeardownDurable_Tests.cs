using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Safety.Tests
{
    /// <summary>
    /// Self-tests of the sweep's Durable oracle (<see cref="TeardownDurable"/>): a cold reopen that Windows refuses
    /// because of a handle the Quiescent oracle already reported is judged on a byte copy of the database files,
    /// and lost data on that copy still fails; any other reopen failure stays <c>durable.REOPEN_FAILED</c>. The
    /// refusal is injected through the oracle's open seam, so these run on every platform; the copy is made while
    /// a live engine still holds the files, as the leftover handle does in the sweep.
    /// </summary>
    public class TeardownDurable_Tests
    {
        [Fact]
        public void A_reopen_blocked_by_a_reported_handle_is_judged_on_a_byte_copy()
        {
            using (var held = new HeldDatabase())
            {
                var result = held.Check(new[] { held.Path }, SharingViolation);

                Assert.Empty(result.Violations);
                var note = Assert.Single(result.Notes);
                Assert.StartsWith("durable: checked on a byte copy", note);
                Assert.True(File.Exists(System.IO.Path.Combine(held.Case.Directory, TeardownDurable.CopyDirectory, "sweep.db")));
            }
        }

        [Fact]
        public void Lost_data_on_the_byte_copy_still_fails_durability()
        {
            using (var held = new HeldDatabase())
            {
                held.Case.Ledger.Acknowledge("rows", 99, TeardownStates.Row(99)); // never written
                held.Db.GetCollection("pending").Insert(TeardownStates.Row(7));
                held.Case.Ledger.Abort("pending", 7, null); // committed after all: an aborted write that is visible

                var result = held.Check(new[] { FileHelper.GetLogFile(held.Path) }, SharingViolation);

                Assert.Contains(result.Violations, item => item.StartsWith("durable.ACKNOWLEDGED_LOST: rows/99", StringComparison.Ordinal));
                Assert.Contains(result.Violations, item => item.StartsWith("durable.ABORTED_VISIBLE: pending/7", StringComparison.Ordinal));
                Assert.Single(result.Notes);
            }
        }

        [Fact]
        public void A_reopen_failure_no_reported_handle_explains_stays_a_violation()
        {
            using (var held = new HeldDatabase())
            {
                // The handle reported open is not the data file or its WAL.
                var unrelated = held.Check(new[] { held.Path + "-shared-live" }, SharingViolation);
                // The data file is reported open, but the reopen failed for another reason.
                var other = held.Check(new[] { held.Path }, path => new IOException("disk failure at " + path));
                // Nothing reported open at all.
                var none = held.Check(new string[0], SharingViolation);

                foreach (var result in new[] { unrelated, other, none })
                {
                    var violation = Assert.Single(result.Violations);
                    Assert.StartsWith("durable.REOPEN_FAILED: the cold reopen failed: IOException", violation);
                    Assert.Empty(result.Notes);
                }
                Assert.False(Directory.Exists(System.IO.Path.Combine(held.Case.Directory, TeardownDurable.CopyDirectory)));
            }
        }

        [Fact]
        public void Only_a_windows_sharing_or_lock_violation_on_the_database_family_counts_as_blocked()
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "durable-family", "sweep.db"));
            var log = FileHelper.GetLogFile(path);
            var sharing = SharingViolation(path);
            var locked = new IOException("locked", unchecked((int)0x80070021));
            var wrapped = new LiteException(0, sharing, "the open failed");

            Assert.True(TeardownDurable.BlockedByReportedHandle(sharing, path, new[] { path }));
            Assert.True(TeardownDurable.BlockedByReportedHandle(sharing, path, new[] { log.ToUpperInvariant() }));
            Assert.True(TeardownDurable.BlockedByReportedHandle(locked, path, new[] { log }));
            Assert.True(TeardownDurable.BlockedByReportedHandle(wrapped, path, new[] { path }));
            Assert.False(TeardownDurable.BlockedByReportedHandle(sharing, path, new[] { FileHelper.GetTempFile(path) }));
            Assert.False(TeardownDurable.BlockedByReportedHandle(sharing, path, new string[0]));
            Assert.False(TeardownDurable.BlockedByReportedHandle(sharing, path, null));
            Assert.False(TeardownDurable.BlockedByReportedHandle(new IOException("other"), path, new[] { path }));
            Assert.False(TeardownDurable.BlockedByReportedHandle(new UnauthorizedAccessException("denied"), path, new[] { path }));
        }

        private static Exception SharingViolation(string path) =>
            new IOException($"The process cannot access the file '{path}' because it is being used by another process.",
                unchecked((int)0x80070020));

        /// <summary>A database with committed rows 1-3 in its WAL whose Direct engine stays open (the leftover handle).</summary>
        private sealed class HeldDatabase : IDisposable
        {
            private readonly LiteEngine _engine;

            public HeldDatabase()
            {
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litedb-durable-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                this.Case = new TeardownCase(new TeardownScenario(), directory, TeardownPrior.Minimal());
                this.Path = System.IO.Path.GetFullPath(this.Case.File("sweep.db"));
                this.Case.DatabasePath = this.Path;
                _engine = new LiteEngine(new EngineSettings { Filename = this.Path });
                this.Db = new LiteDatabase(_engine, disposeOnClose: false);
                for (var id = 1; id <= 3; id++)
                {
                    this.Db.GetCollection("rows").Insert(TeardownStates.Row(id));
                    this.Case.Ledger.Acknowledge("rows", id, TeardownStates.Row(id));
                }
            }

            public TeardownCase Case { get; }
            public string Path { get; }
            public LiteDatabase Db { get; }

            /// <summary>Run the oracle; opening the original file fails with <paramref name="refusal"/>, a copy opens normally.</summary>
            public TeardownRunResult Check(string[] openHandles, Func<string, Exception> refusal)
            {
                var result = new TeardownRunResult();
                TeardownDurable.Check(this.Case, openHandles, result, connection =>
                    string.Equals(System.IO.Path.GetFullPath(connection.Filename), this.Path, StringComparison.Ordinal)
                        ? throw refusal(connection.Filename)
                        : new LiteDatabase(connection));
                return result;
            }

            public void Dispose()
            {
                _engine.Dispose();
                try { Directory.Delete(this.Case.Directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
