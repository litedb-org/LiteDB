#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 11 for encrypted files: through encryption a sector never written back holds zero
    /// ciphertext, which decrypts to one fixed block (AES in ECB mode), not to zeros. A header page
    /// never written back, or only some of its sectors, is restored from the header frame like a
    /// plain one; a sector of any other bytes is refused with both files unchanged.
    /// </summary>
    public partial class HeaderFrame_Tests
    {
        /// <summary>
        /// An encrypted data file whose header page was never written back reads as zeros through its
        /// encryption (a page whose first block is blank reads as zeros), which the header frame completes.
        /// </summary>
        [Fact]
        public void Encrypted_header_page_never_written_back_is_restored()
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Password=secret;Durable Commits=false";
            using (var db = new LiteDatabase(connection))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 12).Select(Row));
            }
            var data = File.ReadAllBytes(file.Filename);
            Array.Clear(data, PAGE_SIZE, PAGE_SIZE); // the header page, after the encryption preamble
            File.WriteAllBytes(file.Filename, data);

            using var reopened = new LiteDatabase(connection);
            reopened.GetCollection("rows").Count().Should().Be(12);
        }

        /// <summary>
        /// The owner's review of 4662fe49f, item 1: an encrypted header page of which only some of its
        /// 16 sectors reached the device (a multi-sector page write is not atomic, even where each
        /// sector's is). A sector never written back holds zero ciphertext, which decrypts to one fixed
        /// block repeated (AES in ECB mode), not to zeros; only a page whose first block is blank reads
        /// as zeros. The header frame completes each such page, with durable commits (only the data
        /// file cannot sync, so the WAL holds every commit) and without, and every row and the index
        /// come back; the database then takes a commit.
        /// </summary>
        [Theory]
        [InlineData("0", false)]
        [InlineData("0-7", false)]
        [InlineData("0-14", false)]
        [InlineData("0,3,9,15", false)]
        [InlineData("15", false)] // its first block blank: the page reads as zeros
        [InlineData("0", true)]
        [InlineData("0,3,9,15", true)]
        public void Encrypted_header_page_partly_written_back_is_restored(string written, bool durable)
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Password=secret" + (durable ? "" : ";Durable Commits=false");
            var data = EncryptedDatabase(file.Filename, connection, durable);
            var kept = Sectors(written);
            for (var sector = 0; sector < PAGE_SIZE / 512; sector++)
                if (!kept.Contains(sector)) Array.Clear(data, PAGE_SIZE + sector * 512, 512);
            File.WriteAllBytes(file.Filename, data);

            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("rows").Count().Should().Be(12);
                db.GetCollection("rows").Count(Query.EQ("value", 3)).Should().Be(2, "the value index is restored too");
                db.GetCollection("rows").Insert(Row(13));
            }
            using var reopened = new LiteDatabase(connection);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, 13));
            reopened.GetCollection("rows").Count(Query.EQ("value", 3)).Should().Be(2);
        }

        /// <summary>
        /// Nothing else is restored: an encrypted header page with a sector of other bytes (here the
        /// same sector of another database's header, as a foreign or stale header would hold) is
        /// neither the header frame's nor never written back, so the open refuses and changes neither file.
        /// So is a sector that only begins like one never written (its first 16 bytes zero ciphertext,
        /// the blank block): the whole sector must be.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Encrypted_header_page_with_a_sector_of_other_bytes_is_refused_unchanged(bool blankFirstBlock)
        {
            using var file = new TempFile();
            using var other = new TempFile();
            var connection = $"Filename={file.Filename};Password=secret;Durable Commits=false";
            var data = EncryptedDatabase(file.Filename, connection, durable: false);
            var foreign = EncryptedDatabase(other.Filename, $"Filename={other.Filename};Password=secret;Durable Commits=false", durable: false);
            for (var sector = 2; sector < PAGE_SIZE / 512; sector++) Array.Clear(data, PAGE_SIZE + sector * 512, 512);
            Buffer.BlockCopy(foreign, PAGE_SIZE + 512, data, PAGE_SIZE + 512, 512);
            if (blankFirstBlock) Array.Clear(data, PAGE_SIZE + 512, 16);
            File.WriteAllBytes(file.Filename, data);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);

            Action open = () =>
            {
                using var db = new LiteDatabase(connection);
                db.GetCollection("rows").Count();
            };
            open.Should().Throw<Exception>("a sector holds bytes of neither the header frame nor a sector never written");
            File.ReadAllBytes(file.Filename).Should().Equal(data, "the refused open writes nothing");
            File.ReadAllBytes(logName).Should().Equal(log);
        }

        /// <summary>
        /// Twelve rows with an index on value, uncheckpointed. With <paramref name="durable"/> the data
        /// file stops syncing after its encryption preamble synced (a writable open syncs that first):
        /// its header never reaches the device, and the WAL holds every commit (decisions 4 and 11).
        /// </summary>
        private static byte[] EncryptedDatabase(string filename, string connection, bool durable)
        {
            var dataPath = Path.GetFullPath(filename);
            var dataSyncs = 0;
            if (durable)
                NativeFileSync.SimulateErrno = path =>
                    string.Equals(Path.GetFullPath(path), dataPath, StringComparison.OrdinalIgnoreCase) && dataSyncs++ > 0 ? 22 : 0;
            try
            {
                using var db = new LiteDatabase(connection);
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 12).Select(Row));
                db.GetCollection("rows").EnsureIndex("value");
            }
            finally { NativeFileSync.SimulateErrno = null; }
            return File.ReadAllBytes(filename);
        }

        /// <summary>Sector numbers like "0-7" or "0,3,9,15".</summary>
        private static int[] Sectors(string list) => list.Split(',').SelectMany(part =>
        {
            var bounds = part.Split('-').Select(int.Parse).ToArray();
            return Enumerable.Range(bounds[0], bounds[bounds.Length - 1] - bounds[0] + 1);
        }).ToArray();

        [Fact]
        public void Encrypted_data_file_left_empty_is_restored_from_its_log()
        {
            using var file = new TempFile();
            var connection = $"Filename={file.Filename};Password=secret;Durable Commits=false";
            using (var db = new LiteDatabase(connection))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 12).Select(Row));
            }
            File.WriteAllBytes(file.Filename, new byte[0]);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));

            // The log's own encryption checks the password before anything is written: a wrong one
            // must not create a data file (a new encryption preamble) beside the log.
            Action wrongPassword = () => new LiteDatabase($"Filename={file.Filename};Password=other").Dispose();
            wrongPassword.Should().Throw<LiteException>();
            new FileInfo(file.Filename).Length.Should().Be(0);
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);

            using (var db = new LiteDatabase(connection))
                db.GetCollection("rows").Count().Should().Be(12);
            wrongPassword.Should().Throw<LiteException>();
        }
    }
}
#endif
