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
    /// Fixture DamagedDocument_5_0_21.zip: written by the LiteDB 5.0.21 package, collection "c" with
    /// {_id: 1..3, a: "keep-i", b: "tail-i-zzz..."}; then the BSON length of string field "b" of
    /// document 2 was overwritten with 0x7FFFFFF0 (one damaged value inside one document).
    ///
    /// 5.0.21: the database opens, documents 1 and 3 are readable, and db.Rebuild() keeps all three
    /// documents - document 2 with the fields read before the damage ({_id: 2, a: "keep-2"}) - and
    /// records one _rebuild_errors entry (this version adds one naming document 2 as kept partially:
    /// it reads like a complete document). A writable open of this version must migrate every index
    /// from its documents, which the damage prevents; it cannot stamp unverified indexes as migrated.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyDamagedDocument_Tests
    {
        /// <summary>
        /// Regression: the failed migration surfaced only an internal ENSURE message and AutoRebuild
        /// could repair the file only on a later open. The failure now names the damaged collection
        /// and the remedies. Opening validation is inspection: the refused open leaves the data file
        /// byte-for-byte unchanged (no rebuild mark, no checkpoint, no log) and a later AutoRebuild
        /// open recovers it explicitly (#3022 opening recovery), without a mark-and-retry.
        /// </summary>
        [Fact]
        public void Default_open_reports_the_damage_and_leaves_the_file_unchanged()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("auto-rebuild=true").And.Contain("legacy index scan=true")
                .And.Contain("was not changed").And.NotContain("marked for rebuild");

            File.ReadAllBytes(file.Filename).Should().Equal(original, "the refused migration must not change anything, not even the rebuild mark");
            File.Exists(FileHelper.GetLogFile(file.Filename)).Should().BeFalse();

            // Refusing again is just as harmless.
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            File.ReadAllBytes(file.Filename).Should().Equal(original);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        [Fact]
        public void Auto_rebuild_repairs_the_damage_on_the_first_open()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            using (var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true"))
            {
                AssertSalvaged(db);
                db.GetCollection("c").Insert(new BsonDocument { ["_id"] = 4 });
            }

            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(original);
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("c").Count().Should().Be(4);
        }

        /// <summary>
        /// A rebuild that may not run (in shared mode: while another connection reads) is not a
        /// failed rebuild: the open reports the damage itself, unwrapped, and changes nothing.
        /// </summary>
        [Fact]
        public void Refused_automatic_rebuild_reports_the_damage_and_changes_nothing()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            var settings = new EngineSettings { Filename = file.Filename, AutoRebuild = true, AutoRebuildAllowed = () => false };
            Action open = () => new LiteEngine(settings).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.NotContain("The automatic rebuild failed");
            error.InnerException.Should().NotBeOfType<AggregateException>();
            error.Data.Contains(LiteEngine.RebuildCauseDataKey).Should().BeFalse();
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
        }

        /// <summary>
        /// Opening recovery is bounded: one attempt in the open that found the damage, and the
        /// installed file is opened without another recovery (no second backup, no rebuild loop).
        /// </summary>
        [Fact]
        public void Auto_rebuild_runs_once_and_never_rebuilds_its_result_again()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            var attempts = 0;
            var settings = new EngineSettings { Filename = file.Filename, AutoRebuild = true, AutoRebuildAllowed = () => ++attempts > 0 };
            using (var db = new LiteDatabase(new LiteEngine(settings))) AssertSalvaged(db);
            attempts.Should().Be(1);

            settings.AutoRebuildAllowed = () => throw new InvalidOperationException("must not rebuild again");
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                AssertSalvaged(db);
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(original);
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", true)).Should().BeFalse();
        }

        [Fact]
        public void Caller_stream_is_neither_marked_nor_rebuilt()
        {
            var original = Fixture();
            using var data = new MemoryStream();
            data.Write(original, 0, original.Length);
            data.Position = 0;

            Action open = () => new LiteEngine(new EngineSettings { DataStream = data, AutoRebuild = true }).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("A stream is not rebuilt in place").And.NotContain("automatic rebuild");
            error.InnerException.Should().NotBeOfType<AggregateException>();
            data.ToArray().Should().Equal(original, "a caller's stream is never marked or rebuilt");

            // A later open reports the damage again, with remedies that work for a stream.
            data.Position = 0;
            var again = open.Should().Throw<LiteException>().Which;
            again.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            again.Message.Should().Contain("Collection 'c'").And.Contain("A stream is not rebuilt in place");
            data.ToArray().Should().Equal(original);
        }

        [Fact]
        public void Failed_opening_recovery_keeps_a_non_LiteDB_exception_type()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            // e.g. a transient Windows lock violation, which the shared engine's open retries on
            var settings = new EngineSettings { Filename = file.Filename, AutoRebuild = true, AutoRebuildAllowed = () => throw new IOException("sharing violation") };
            Action open = () => new LiteEngine(settings).Dispose();
            open.Should().Throw<IOException>().WithMessage("sharing violation");
            File.ReadAllBytes(file.Filename).Should().Equal(original, "nothing was written, not even a rebuild mark");

            // The next AutoRebuild open still finds the damage and rebuilds.
            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        [Fact]
        public void Failed_opening_recovery_keeps_the_data_of_its_LiteException()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            var settings = new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = true,
                AutoRebuildAllowed = () =>
                {
                    var failure = new LiteException(0, "rebuild state");
                    failure.Data["state"] = "kept";
                    throw failure;
                }
            };
            Action open = () => new LiteEngine(settings).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("The automatic rebuild failed: rebuild state");
            error.InnerException.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().HaveCount(2);
            error.Data["state"].Should().Be("kept");
            File.ReadAllBytes(file.Filename).Should().Equal(original);
        }

        /// <summary>
        /// An opening recovery that fails with an exception callers retry on (here an IOException)
        /// keeps its type and HResult; the damage that required the rebuild is not lost with it.
        /// </summary>
        [Fact]
        public void Failed_opening_recovery_with_an_io_error_keeps_the_damage_in_its_data()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            var settings = new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = true,
                AutoRebuildAllowed = () => throw new IOException("transient rebuild failure")
            };
            Action open = () => new LiteEngine(settings).Dispose();
            var error = open.Should().Throw<IOException>().Which;
            error.Message.Should().Be("transient rebuild failure");
            error.Data[LiteEngine.RebuildCauseDataKey].As<string>().Should().Contain("Collection 'c'");
            File.ReadAllBytes(file.Filename).Should().Equal(original);
        }

        [Fact]
        public void Read_only_legacy_scan_surfaces_the_damage_without_changing_the_file()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            using (var db = new LiteDatabase($"Filename={file.Filename};ReadOnly=true;Legacy Index Scan=true"))
            {
                // Unmigrated indexes are not trusted, so every query scans the collection; the
                // damaged document is reported (and stops the engine), never skipped.
                Action scan = () => db.GetCollection("c").FindAll().ToList();
                scan.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "a read-only open never marks or changes the file");
        }

        /// <summary>
        /// Real read-only inspection never writes the invalid-state mark, not even when the damage
        /// stops the engine and its streams could be written (dev marked such a caller stream).
        /// </summary>
        [Fact]
        public void Read_only_engine_over_writable_streams_never_marks_the_damage_it_finds()
        {
            var original = Fixture();
            using var data = new MemoryStream();
            data.Write(original, 0, original.Length);
            using var log = new MemoryStream();

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, ReadOnly = true, LegacyIndexScan = true }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Action scan = () => db.GetCollection("c").FindAll().ToList();
                scan.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            }
            data.ToArray().Should().Equal(original, "a read-only engine never writes the rebuild mark");
            log.Length.Should().Be(0);
        }

        /// <summary>
        /// Regression: FileReaderV8 discarded a document whose BSON read failed
        /// (FileReaderV8.Documents.cs, 05d34f058); 5.0.21 yielded the partial document.
        /// </summary>
        [Fact]
        public void Rebuild_keeps_the_readable_prefix_of_a_damaged_document()
        {
            using var file = new TempFile();
            var bytes = Fixture();
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1; // request rebuild on open (AutoRebuild)
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        /// <summary>
        /// A partial document must not displace a complete one: document 2's _id is patched to 3,
        /// so its readable part {_id: 3, a: "keep-2"} precedes the complete document 3 on the page.
        /// </summary>
        [Fact]
        public void Rebuild_keeps_the_complete_document_when_a_damaged_one_has_its_id()
        {
            using var file = new TempFile();
            var bytes = Fixture();
            var id2 = new byte[] { 0x10, (byte)'_', (byte)'i', (byte)'d', 0, 2, 0, 0, 0 };
            var at = Enumerable.Range(0, bytes.Length - id2.Length).Single(i => bytes.Skip(i).Take(id2.Length).SequenceEqual(id2));
            bytes[at + 5] = 3;
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            var col = db.GetCollection("c");
            col.Count().Should().Be(2);
            col.FindById(3)["b"].AsString.Should().StartWith("tail-3-");
            db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                .Should().Contain(x => x.Contains("damaged document 3 was not kept"));
        }

        /// <summary>
        /// Fixture DamagedUniqueDocuments_5_0_21.zip: written by the LiteDB 5.0.21 package, collection
        /// "c" with unique index "b" and {_id: 1..4, a: "keep-i", b: "u-i"}; then the BSON length of
        /// string field "a" of documents 2 and 3 was overwritten with 0x7FFFFFF0, so only their _id
        /// is readable. 5.0.21's rebuild fails ("duplicate key in unique index 'b'", value null).
        /// The first readable part is kept; the second, which would repeat the null key, is reported.
        /// </summary>
        [Fact]
        public void Rebuild_reports_a_damaged_document_that_would_break_a_unique_index()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("DamagedUniqueDocuments_5_0_21.zip"));

            using (var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true"))
            {
                var col = db.GetCollection("c");
                col.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2, 4 });
                col.FindById(2).Keys.Should().BeEquivalentTo(new[] { "_id" });
                col.FindById(4)["b"].AsString.Should().Be("u-4");
                var errors = db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString).ToList();
                errors.Should().Contain(x => x.Contains("damaged document 3 was not kept") && x.Contains("unique index 'b'"));
                errors.Should().Contain(x => x.Contains("Only the readable part of damaged document 2 was kept"));
                errors.Should().NotContain(x => x.Contains("Only the readable part of damaged document 3"));
                col.Insert(new BsonDocument { ["_id"] = 5, ["b"] = "u-5" });
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("c").Count(Query.EQ("b", "u-5")).Should().Be(1);
            reopened.GetCollection("$indexes").Find(Query.EQ("name", "b")).Single()["unique"].AsBoolean.Should().BeTrue();
        }

        /// <summary>
        /// Fixture DamagedSalvageDuplicate_5_0_21.zip: written by the LiteDB 5.0.21 package, collection
        /// "c" with unique index "b" and {_id: 1, a: "keep-1", b: "u-1"}, {_id: 7, b: "u-7", a: "tail-7"},
        /// {_id: 8, a: "keep-8", b: "u-8"} and {_id: 9, a: "keep-9", b: "u-9"}; then "b" of document 7
        /// was changed to "u-1", the _id of document 8 to 7, and the BSON length of "a" of both was
        /// overwritten with 0x7FFFFFF0. The first readable part, {_id: 7, b: "u-1"}, repeats document
        /// 1's unique key and is reported. It still reserved _id 7, so the second, {_id: 7}, which
        /// fits, was reported as a duplicate and lost too. It is now kept.
        /// </summary>
        [Fact]
        public void Rejected_readable_part_leaves_its_id_to_a_later_one()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("DamagedSalvageDuplicate_5_0_21.zip"));

            using (var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true"))
            {
                var col = db.GetCollection("c");
                col.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 7, 9 });
                col.FindById(7).Keys.Should().BeEquivalentTo(new[] { "_id" }, "the second readable part is kept");
                col.FindById(1)["a"].AsString.Should().Be("keep-1");
                col.FindById(9)["b"].AsString.Should().Be("u-9");
                var errors = db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString).ToList();
                errors.Should().Contain(x => x.Contains("damaged document 7 was not kept") && x.Contains("unique index 'b'"));
                errors.Should().Contain(x => x.Contains("Only the readable part of damaged document 7 was kept"));
                errors.Should().NotContain(x => x.Contains("another document has the same _id"));
            }

            using var reopened = new LiteDatabase(file.Filename);
            var c = reopened.GetCollection("c");
            c.Find(Query.EQ("b", "u-1")).Select(x => x["_id"].AsInt32).Should().Equal(1);
            c.Find(Query.EQ("b", BsonValue.Null)).Select(x => x["_id"].AsInt32).Should().Equal(7);
        }

        /// <summary>
        /// The same fixture with "b" of document 7 back at "u-7": its readable part fits and is kept,
        /// so the later part with _id 7 is reported as a duplicate. Only a rejected part frees its _id.
        /// </summary>
        [Fact]
        public void Readable_part_with_the_id_of_a_kept_one_is_reported_as_a_duplicate()
        {
            using var file = new TempFile();
            var bytes = Fixture("DamagedSalvageDuplicate_5_0_21.zip");
            // {_id: 7, b: "u-1"}: the _id element, then b's string.
            var changed = System.Text.Encoding.ASCII.GetBytes("\u0010_id\0\u0007\0\0\0\u0002b\0\u0004\0\0\0u-1\0");
            var at = Enumerable.Range(0, bytes.Length - changed.Length).Single(i => bytes.Skip(i).Take(changed.Length).SequenceEqual(changed));
            bytes[at + changed.Length - 2] = (byte)'7';
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            var col = db.GetCollection("c");
            col.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 7, 9 });
            col.FindById(7)["b"].AsString.Should().Be("u-7", "the first readable part is kept");
            var errors = db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString).ToList();
            errors.Should().Contain(x => x.Contains("damaged document 7 was not kept: another document has the same _id"));
            errors.Count(x => x.Contains("Only the readable part of damaged document 7 was kept")).Should().Be(1);
        }

        /// <summary>
        /// Fixture DamagedIndexKeys_5_0_21.zip: written by the LiteDB 5.0.21 package, collections
        /// "maxed", "mined", "long" and "throws" with {_id: 1..3, a: "keep-i", b: "u-i"} and an index
        /// "k" on COALESCE($.b, MAXVALUE()), COALESCE($.b, MINVALUE()), COALESCE($.b, 1100 characters) and
        /// SUBSTRING(COALESCE($.b, 'x'), 1, 2); then the BSON length of "b" of document 2 was
        /// overwritten in each. The readable part {_id: 2, a: "keep-2"} has an invalid key, a key
        /// too long, or a key that cannot be computed: it is reported, never inserted.
        /// </summary>
        [Fact]
        public void Rebuild_reports_a_damaged_document_whose_keys_cannot_be_indexed()
        {
            using var file = new TempFile();
            var bytes = Fixture("DamagedIndexKeys_5_0_21.zip");
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            File.WriteAllBytes(file.Filename, bytes);

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            var errors = db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString).ToList();
            foreach (var (collection, reason) in new[]
            {
                ("maxed", "its key is not valid in index 'k'"),
                ("mined", "its key is not valid in index 'k'"),
                ("long", "its key is not valid in index 'k'"),
                ("throws", "the keys of index 'k' cannot be computed"),
            })
            {
                var col = db.GetCollection(collection);
                col.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 }, collection);
                db.GetCollection("$indexes").Find(Query.And(Query.EQ("collection", collection), Query.EQ("name", "k"))).Should().ContainSingle();
                errors.Should().Contain(x => x.Contains("damaged document 2 was not kept") && x.Contains(reason), collection);
            }
        }

        private static void AssertSalvaged(LiteDatabase db)
        {
            var col = db.GetCollection("c");
            col.Count().Should().Be(3);
            col.FindById(1)["b"].AsString.Should().StartWith("tail-1-");
            var partial = col.FindById(2);
            partial["a"].AsString.Should().Be("keep-2");
            partial.ContainsKey("b").Should().BeFalse();
            db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                .Should().Contain(x => x.Contains("Only the readable part of damaged document 2 was kept"), "it reads like a complete document");
        }

        private static byte[] Fixture(string name = "DamagedDocument_5_0_21.zip")
        {
            using var zip = new ZipArchive(File.OpenRead(ArtifactFixtures.Path(name)), ZipArchiveMode.Read);
            using var entry = zip.GetEntry("damaged.db").Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
