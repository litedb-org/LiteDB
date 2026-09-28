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
    /// records one _rebuild_errors entry. A writable open of this version must migrate every index
    /// from its documents, which the damage prevents; it cannot stamp unverified indexes as migrated.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyDamagedDocument_Tests
    {
        /// <summary>
        /// Regression: the failed migration surfaced only an internal ENSURE message and AutoRebuild
        /// could repair the file only on a later open. The failure now names the damaged collection
        /// and the remedies, and changes nothing but the rebuild mark (which 5.0.21 ignores unless
        /// its own AutoRebuild is set).
        /// </summary>
        [Fact]
        public void Default_open_reports_the_damage_and_only_marks_the_file_for_rebuild()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            Action open = () => new LiteDatabase(file.Filename).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("auto-rebuild=true").And.Contain("legacy index scan=true");

            var after = File.ReadAllBytes(file.Filename);
            after[HeaderPage.P_INVALID_DATAFILE_STATE].Should().Be(1);
            after[HeaderPage.P_INVALID_DATAFILE_STATE] = original[HeaderPage.P_INVALID_DATAFILE_STATE];
            after.Should().Equal(original, "the refused migration must not change anything else");
            File.Exists(FileHelper.GetLogFile(file.Filename)).Should().BeFalse();

            using var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true");
            AssertSalvaged(db);
        }

        [Fact]
        public void Auto_rebuild_repairs_the_damage_on_the_first_open()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

            using (var db = new LiteDatabase($"Filename={file.Filename};Auto-Rebuild=true"))
            {
                AssertSalvaged(db);
                db.GetCollection("c").Insert(new BsonDocument { ["_id"] = 4 });
            }

            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeTrue();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("c").Count().Should().Be(4);
        }

        [Fact]
        public void Failed_automatic_rebuild_keeps_the_damage_that_required_it()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

            // A rebuild that cannot run (in shared mode: while another connection reads).
            var settings = new EngineSettings { Filename = file.Filename, AutoRebuild = true, AutoRebuildAllowed = () => false };
            Action open = () => new LiteEngine(settings).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.Contain("The automatic rebuild failed");
            error.InnerException.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().HaveCount(2);
        }

#if DEBUG || TESTING
        [Fact]
        public void Open_is_not_repeated_when_the_rebuild_mark_cannot_be_written()
        {
            using var file = new TempFile();
            var original = Fixture();
            File.WriteAllBytes(file.Filename, original);

            EngineState.SimulateProcessCrash = phase =>
            {
                if (phase == "invalid-state-before-mark") throw new InvalidOperationException("mark write failed");
            };
            try
            {
                Action open = () => new LiteEngine(new EngineSettings { Filename = file.Filename, AutoRebuild = true }).Dispose();
                var error = open.Should().Throw<LiteException>().Which;
                error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
                error.Message.Should().Contain("Collection 'c'").And.NotContain("automatic rebuild");
                error.InnerException.Should().NotBeOfType<AggregateException>();
            }
            finally { EngineState.SimulateProcessCrash = null; }
            File.ReadAllBytes(file.Filename).Should().Equal(original, "neither the mark nor a rebuild was written");
        }
#endif

        [Fact]
        public void Caller_stream_is_marked_but_never_rebuilt()
        {
            var original = Fixture();
            using var data = new MemoryStream();
            data.Write(original, 0, original.Length);
            data.Position = 0;

            Action open = () => new LiteEngine(new EngineSettings { DataStream = data, AutoRebuild = true }).Dispose();
            var error = open.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            error.Message.Should().Contain("Collection 'c'").And.NotContain("automatic rebuild");
            error.InnerException.Should().NotBeOfType<AggregateException>();

            // The stream now carries the mark: a later open reports the damage again, with remedies
            // that work for a stream, instead of failing inside a file rebuild.
            data.Position = 0;
            var again = open.Should().Throw<LiteException>().Which;
            again.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            again.Message.Should().Contain("Collection 'c'").And.Contain("A stream is not rebuilt in place");
        }

        [Fact]
        public void Failed_repeated_open_keeps_a_non_LiteDB_exception_type()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

            // e.g. a transient Windows lock violation, which the shared engine's open retries on
            var settings = new EngineSettings { Filename = file.Filename, AutoRebuild = true, AutoRebuildAllowed = () => throw new IOException("sharing violation") };
            Action open = () => new LiteEngine(settings).Dispose();
            open.Should().Throw<IOException>().WithMessage("sharing violation");
            File.ReadAllBytes(file.Filename)[HeaderPage.P_INVALID_DATAFILE_STATE].Should().Be(1, "the next open still rebuilds");
        }

        [Fact]
        public void Failed_repeated_open_keeps_the_data_of_its_LiteException()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture());

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
            error.Message.Should().Contain("Collection 'c'").And.Contain("The automatic rebuild failed: rebuild state");
            error.Data["state"].Should().Be("kept");
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
                db.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                    .Should().Contain(x => x.Contains("damaged document 3 was not kept") && x.Contains("unique index 'b'"));
                col.Insert(new BsonDocument { ["_id"] = 5, ["b"] = "u-5" });
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("c").Count(Query.EQ("b", "u-5")).Should().Be(1);
            reopened.GetCollection("$indexes").Find(Query.EQ("name", "b")).Single()["unique"].AsBoolean.Should().BeTrue();
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
            db.GetCollection("_rebuild_errors").Count().Should().BeGreaterOrEqualTo(1);
        }

        private static byte[] Fixture(string name = "DamagedDocument_5_0_21.zip")
        {
            using var resource = typeof(LegacyDamagedDocument_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources." + name);
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry("damaged.db").Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
