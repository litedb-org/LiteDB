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
    /// A 5.0.21 data file beside the WAL its conversion wrote: the converted header never reached
    /// the device while the checksummed frames written after it did (storage that cannot sync,
    /// #2242, with a power loss or the OS writing the log back first). The open read those frames
    /// by legacy rules, as 8 KiB pages whose page IDs came from frame trailers, and wrote them into
    /// the data file at those positions: a 6.7 TB sparse file on Linux, "There is not enough space
    /// on the disk" on Windows, and the log deleted. The open is now refused and changes neither file.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class ConvertedWalBesideLegacyHeader_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("migration-power-loss", false)]
        [InlineData("migration-power-loss", true)]
        public void Legacy_header_beside_a_converted_wal_is_refused(string password, bool readOnly)
        {
            var legacy = Fixture(password == null ? "plain.db" : "encrypted.db");
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                File.WriteAllBytes(file.Filename, legacy);
                byte[] wal;
                using (var converted = new LiteDatabase(Connection(file.Filename, password, false)))
                {
                    converted.CheckpointSize = 0;
                    converted.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 900002 });
                    wal = SyncPowerLossModel.ReadShared(logName);
                }
                wal.Length.Should().BeGreaterOrEqualTo(WalChecksum.FrameSize);

                // The image a power loss leaves: the legacy header, and the WAL written after it.
                File.WriteAllBytes(file.Filename, legacy);
                File.WriteAllBytes(logName, wal);
                Action open = () => new LiteDatabase(Connection(file.Filename, password, readOnly)).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATABASE)
                    .WithMessage("*log file holds WAL frames of a converted database*legacy (v5) header*");
                File.ReadAllBytes(file.Filename).Should().Equal(legacy);
                File.ReadAllBytes(logName).Should().Equal(wal);

                // The remedy the diagnostic names.
                File.Delete(logName);
                using var reopened = new LiteDatabase(Connection(file.Filename, password, readOnly));
                reopened.GetCollection("rows").Count().Should().BeGreaterThan(0);
                (reopened.GetCollection("rows").FindById(900002) == null).Should().BeTrue();
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The recognised frame checks its own trailer: a changed page byte, CRC or position is not
        /// a frame (a legacy WAL chunk would have to match all of them).
        /// </summary>
        [Fact]
        public void Frame_is_recognised_by_its_magic_position_and_crc()
        {
            var legacy = Fixture("plain.db");
            using var data = new MemoryStream();
            data.Write(legacy, 0, legacy.Length);
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 900002 });
                var wal = log.ToArray();
                var frame = wal.Take(WalChecksum.FrameSize).ToArray();
                WalChecksum.IsFrame(frame, 0).Should().BeTrue();
                WalChecksum.IsFrame(frame, Constants.PAGE_SIZE).Should().BeFalse("another position");
                foreach (var offset in new[] { 100, Constants.PAGE_SIZE + 4, Constants.PAGE_SIZE + 60 })
                {
                    var changed = (byte[])frame.Clone();
                    changed[offset] ^= 1;
                    WalChecksum.IsFrame(changed, 0).Should().BeFalse("byte {0} changed", offset);
                }
            }
        }

        private static string Connection(string filename, string password, bool readOnly) =>
            $"Filename={filename}" + (password == null ? "" : $";Password={password}") +
            (readOnly ? ";readonly=true;legacy index scan=true" : "");

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(ConvertedWalBesideLegacyHeader_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
