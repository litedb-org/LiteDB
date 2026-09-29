#if DEBUG || TESTING
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>The legacy conversion keeps the legacy header's backup and its journal until the converted header synced.</summary>
    public partial class KeptWalStop_Tests
    {
        /// <summary>
        /// A 5.0.21 file whose data file syncs for the conversion's first two syncs, not for the sync
        /// of its converted header: the conversion emptied the log (the legacy header's backup and
        /// its journal) anyway. It now stops with both kept, and the open falls back to read-only
        /// ("Cannot convert this legacy database now"): it reads every document, refuses writes and
        /// writes nothing more. The power-loss image and, once the data file syncs, the database
        /// itself open converted with every document.
        /// </summary>
        [Fact]
        public void Conversion_whose_header_sync_fails_opens_read_only_until_the_data_file_syncs()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            var expected = LegacyContents(file.Filename);
            try
            {
                (byte[] Data, byte[] Log) image;
                using (var power = new FilePowerLossModel(file.Filename) { DataFailsFromSync = 3 })
                {
                    byte[] data, log;
                    using (var db = new LiteDatabase(file.Filename))
                    {
                        power.DataSyncs.Should().BeGreaterOrEqualTo(3, "the header's sync was reached");
                        (data, log) = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                        log.Length.Should().BeGreaterThan(0, "the legacy header backup and the journal are kept");
                        UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused,
                            x =>
                            {
                                Contents(x).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
                                UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x);
                            }).Should().Contain("cannot sync");
                    }
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the read-only engine wrote nothing");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(log);
                    image = power.Capture();
                }
                image.Data.Should().Equal(original, "the converted header never synced");
                FilePowerLossModel.Open(image, db => AssertConverted(db, expected));
                using var converted = new LiteDatabase(file.Filename);
                AssertConverted(converted, expected);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A WAL in memory (LiteDatabase(Stream) over a caller FileStream) survives no power loss,
        /// so nothing waits for a data sync on its behalf: a 5.0.21 file converts on a data file
        /// that cannot sync, and the WAL is still emptied by checkpoints.
        /// </summary>
        [Fact]
        public void Legacy_file_stream_with_an_in_memory_wal_converts_on_a_data_file_that_cannot_sync()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("plain.db"));
            var expected = LegacyContents(file.Filename);
            var rejected = 0;
            NativeFileSync.SimulateErrno = _ => { rejected++; return 22; };
            try
            {
                using var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite);
                using var db = new LiteDatabase(stream);
                AssertConverted(db, expected);
                rejected.Should().BeGreaterThan(0, "the data file answered \"cannot sync\"");
                db.CheckpointSize = 10;
                for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                Info(db)["walKept"].AsBoolean.Should().BeFalse();
                Info(db)["logFileSize"].AsInt64.Should().BeLessThan(40 * PAGE_SIZE, "the in-memory WAL is still emptied");
                db.GetCollection("log").Count().Should().Be(200);
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        private static Dictionary<string, string[]> LegacyContents(string filename)
        {
            using var legacy = new LiteDatabase($"Filename={filename};ReadOnly=true;Legacy Index Scan=true");
            var contents = Contents(legacy);
            contents.Values.Sum(x => x.Length).Should().BeGreaterThan(0);
            return contents;
        }

        private static Dictionary<string, string[]> Contents(LiteDatabase db) => db.GetCollectionNames().ToDictionary(name => name,
            name => db.GetCollection(name).FindAll().OrderBy(x => x["_id"]).Select(x => JsonSerializer.Serialize(x)).ToArray());

        /// <summary>The database is converted and holds exactly <paramref name="expected"/>.</summary>
        private static int AssertConverted(LiteDatabase db, Dictionary<string, string[]> expected)
        {
            Info(db)["checksums"].AsBoolean.Should().BeTrue();
            Contents(db).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
            return expected.Count;
        }

        private static byte[] Fixture(string name)
        {
            using var resource = typeof(KeptWalStop_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
