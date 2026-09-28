using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Legacy (5.x) WAL pages carry no checksum, so a torn or foreign page at the end of the WAL
    /// can name any page ID and any type; one of 0xCD bytes names page 0xCDCDCDCD and reads as
    /// committed. The open replayed it and the drain wrote it about 28 TB into the data file (as
    /// 5.0.21 would); a committed page 0 that is not a header replaced the data file's header. A
    /// committed legacy page that is not a page, or lies beyond every page the data and log files
    /// can hold, now fails the open, changing neither file.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class LegacyWalPageBound_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Committed_legacy_page_beyond_both_files_fails_the_open(bool readOnly)
        {
            var page = Page(PageType.Data, 0xCDCDCDCD);
            AssertRefused(page, readOnly, "*committed page (ID 3452816845)*up to *move the log file aside*");
        }

        /// <summary>
        /// A torn page of 0xCD bytes (an unknown type), a committed page 0 of another type (here a
        /// data page, which the drain wrote over the data file's header before deleting the log,
        /// leaving a file no open accepts), and a header naming another page.
        /// </summary>
        [Theory]
        [InlineData("torn", 3452816845u)]
        [InlineData("data page 0", 0u)]
        [InlineData("header page 7", 7u)]
        public void Committed_legacy_page_that_is_not_a_page_fails_the_open(string kind, uint pageID)
        {
            var page = kind == "torn" ? Enumerable.Repeat((byte)0xCD, Constants.PAGE_SIZE).ToArray()
                : Page(kind == "data page 0" ? PageType.Data : PageType.Header, pageID);
            AssertRefused(page, false, $"*commits a page (ID {pageID}) that is not a valid page*move the log file aside*");
        }

        /// <summary>The fixture's crash WAL with one more committed page appended.</summary>
        private static void AssertRefused(byte[] page, bool readOnly, string message)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var data = Entry("crash.db");
            var log = Entry("crash-log.db").Concat(page).ToArray();
            try
            {
                File.WriteAllBytes(file.Filename, data);
                File.WriteAllBytes(logName, log);
                var connection = $"Filename={file.Filename}" + (readOnly ? ";readonly=true;legacy index scan=true" : "");

                Action open = () => new LiteDatabase(connection).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATABASE)
                    .WithMessage(message);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);

                // Control: the same WAL without the appended page opens with every commit.
                File.WriteAllBytes(logName, Entry("crash-log.db"));
                using var db = new LiteDatabase(connection);
                var docs = db.GetCollection("docs").FindAll().ToList();
                docs.Should().HaveCount(101);
                docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>A committed page of this type and ID in a transaction of its own.</summary>
        private static byte[] Page(PageType type, uint pageID)
        {
            var page = new byte[Constants.PAGE_SIZE];
            BitConverter.GetBytes(pageID).CopyTo(page, BasePage.P_PAGE_ID);
            page[BasePage.P_PAGE_TYPE] = (byte)type;
            BitConverter.GetBytes(0x7FFF0000u).CopyTo(page, BasePage.P_TRANSACTION_ID);
            page[BasePage.P_IS_CONFIRMED] = 1;
            return page;
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
