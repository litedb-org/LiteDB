using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Legacy (5.x) WAL pages carry no checksum, so a torn or foreign page at the end of the WAL
    /// can name any page ID; one of 0xCD bytes names page 0xCDCDCDCD and reads as committed. The
    /// open replayed it and the drain wrote it about 28 TB into the data file (as 5.0.21 would).
    /// A committed legacy page beyond every page the data and log files can hold now fails the
    /// open, changing neither file.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class LegacyWalPageBound_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Committed_legacy_page_beyond_both_files_fails_the_open(bool readOnly)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var data = Entry("crash.db");
            var log = Entry("crash-log.db").Concat(Enumerable.Repeat((byte)0xCD, Constants.PAGE_SIZE)).ToArray();
            try
            {
                File.WriteAllBytes(file.Filename, data);
                File.WriteAllBytes(logName, log);
                var connection = $"Filename={file.Filename}" + (readOnly ? ";readonly=true;legacy index scan=true" : "");

                Action open = () => new LiteDatabase(connection).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATABASE)
                    .WithMessage("*committed page (ID 3452816845)*move the log file aside*");
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);

                // Control: the same WAL without the foreign page opens with every commit.
                File.WriteAllBytes(logName, Entry("crash-log.db"));
                using var db = new LiteDatabase(connection);
                var docs = db.GetCollection("docs").FindAll().ToList();
                docs.Should().HaveCount(101);
                docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// ConcurrentWalCrash_5_0_21.zip, written by the LiteDB 5.0.21 package: a worker thread held an
        /// explicit transaction on "a" open (300 inserts, page IDs taken from the shared header but
        /// never written), the main thread committed 3 inserts into "b" (new pages 58 and 59), then
        /// the process was killed (Environment.FailFast). The data file has 7 pages (LastPageID 6),
        /// the WAL 13; pages 7..57 were handed out but never written. 5.0.21 reopens the pair with
        /// b = 13 documents. The bound counts pages up to a committed header's LastPageID.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Committed_pages_after_ones_an_open_transaction_took_stay_within_the_bound(bool readOnly)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                File.WriteAllBytes(file.Filename, Entry("c.db", "ConcurrentWalCrash_5_0_21.zip"));
                File.WriteAllBytes(logName, Entry("c-log.db", "ConcurrentWalCrash_5_0_21.zip"));
                var connection = $"Filename={file.Filename}" + (readOnly ? ";readonly=true;legacy index scan=true" : "");
                using var db = new LiteDatabase(connection);
                db.GetCollection("b").Count().Should().Be(13);
                db.GetCollection("b").Count(Query.GTE("_id", 100)).Should().Be(3, "the commits the WAL alone holds");
                db.GetCollection("a").Count().Should().Be(10);
            }
            finally { File.Delete(logName); }
        }

        private static byte[] Entry(string name, string archive = "WalCrash_5_0_21.zip")
        {
            using var zip = new ZipArchive(typeof(LegacyWalPageBound_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources." + archive), ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
