using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A 5.0.21 crash image whose WAL ends in a partial page. The conversion drains the legacy WAL
    /// with a checkpoint that appends a header journal at the physical WAL end. Before the partial
    /// page is trimmed, journal bytes complete the torn frame: when it is a prefix of a confirming
    /// page, legacy recovery (this engine's and 5.0.21's) takes it as a committed transaction and
    /// applies journal bytes as a data page, after any interruption of that first writable open.
    /// An encrypted WAL tail that is not a whole AES block failed the journal outright.
    /// The drain trims partial pages right after it decided to run, before its first write.
    ///
    /// Fixtures written by the LiteDB 5.0.21 package: WalCrash_5_0_21.zip (see
    /// LegacyWalSharedMigration_Tests: 101 documents, 21 with value 7 only in the WAL) and
    /// EncryptedWalCrash_5_0_21.zip (password "wal-secret": 65 documents, 15 with value 7 only in
    /// the WAL; 5.0.21 recovers both counts).
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyWalTornTail_Tests
    {
        [Theory]
        [InlineData(100, false)] // garbage bytes
        [InlineData(512, true)]  // prefix of a confirming page of a new transaction
        [InlineData(4096, true)]
        public void Every_crash_image_of_converting_a_wal_with_a_torn_tail_recovers(int tail, bool confirmingPage)
        {
            var data = Entry("WalCrash_5_0_21.zip", "crash.db");
            var log = WithTornTail(Entry("WalCrash_5_0_21.zip", "crash-log.db"), tail, confirmingPage);

            using var device = new IndexMigrationCrashDevice(data, log);
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = device.Data, LogStream = device.Log })))
            {
                Check(db).Should().Be((101, 21));
            }
            device.Armed = false;
            device.Images.Should().NotBeEmpty();

            foreach (var image in device.Images)
            {
                using var imageData = ChecksumTestFiles.Copy(image.Data);
                using var imageLog = ChecksumTestFiles.Copy(image.Log);
                for (var open = 0; open < 2; open++)
                {
                    using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = imageData, LogStream = imageLog }));
                    Check(db).Should().Be((101, 21), image.Event);
                }
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(512)]
        [InlineData(4096)]
        public void Data_write_failure_during_the_drain_leaves_a_recoverable_file(int tail)
        {
            using var data = new FailingWrites(Entry("WalCrash_5_0_21.zip", "crash.db"));
            using var log = ChecksumTestFiles.Copy(WithTornTail(Entry("WalCrash_5_0_21.zip", "crash-log.db"), tail, true));

            Action open = () => new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }).Dispose();
            open.Should().Throw<IOException>();

            // Space was freed: reopen what the failed open left behind.
            using var dataCopy = ChecksumTestFiles.Copy(data.ToArray());
            using var logCopy = ChecksumTestFiles.Copy(log.ToArray());
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = dataCopy, LogStream = logCopy }));
            Check(db).Should().Be((101, 21));
        }

        [Theory]
        [InlineData(100)]
        [InlineData(16)]
        public void Encrypted_wal_with_a_torn_tail_converts(int tail)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Entry("EncryptedWalCrash_5_0_21.zip", "crash.db"));
            File.WriteAllBytes(FileHelper.GetLogFile(file.Filename),
                Entry("EncryptedWalCrash_5_0_21.zip", "crash-log.db").Concat(Enumerable.Repeat((byte)0xCD, tail)).ToArray());

            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase($"Filename={file.Filename};Password=wal-secret");
                var docs = db.GetCollection("docs");
                docs.Count().Should().Be(65);
                docs.Count(Query.EQ("value", 7)).Should().Be(15);
            }
        }

        /// <summary>Append a torn frame: garbage, or the prefix of a copy of a WAL page restamped
        /// as the confirming page of a new transaction 22.</summary>
        private static byte[] WithTornTail(byte[] log, int tail, bool confirmingPage)
        {
            if (tail == 0) return log;
            var torn = confirmingPage
                ? log.Skip(log.Length - 3 * Constants.PAGE_SIZE).Take(tail).ToArray()
                : Enumerable.Repeat((byte)0xCD, tail).ToArray();
            if (confirmingPage)
            {
                BitConverter.GetBytes(22u).CopyTo(torn, BasePage.P_TRANSACTION_ID);
                torn[BasePage.P_IS_CONFIRMED] = 1;
            }
            return log.Concat(torn).ToArray();
        }

        private static (int count, int updated) Check(LiteDatabase db)
        {
            var docs = db.GetCollection("docs").FindAll().ToList();
            return (docs.Count, docs.Count(x => x["value"].AsInt32 == 7));
        }

        private static byte[] Entry(string fixture, string name)
        {
            using var resource = typeof(LegacyWalTornTail_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources." + fixture);
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        /// <summary>A data file on a full disk: every write fails before anything is written.</summary>
        private sealed class FailingWrites : MemoryStream
        {
            internal FailingWrites(byte[] bytes)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new IOException("There is not enough space on the disk.");
        }
    }
}
