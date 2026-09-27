using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2812CollationStamp_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Changed_sort_stamp_rejects_open_without_changing_data(bool readOnly)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = "a" });
            var bytes = File.ReadAllBytes(file.Filename);
            BitConverter.ToUInt32(bytes, EnginePragmas.P_COLLATION_STAMP).Should().NotBe(0);
            bytes[EnginePragmas.P_COLLATION_STAMP] ^= 1;
            PageChecksum.Write(new BufferSlice(bytes, 0, Constants.PAGE_SIZE));
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = readOnly }); };
            open.Should().Throw<LiteException>().WithMessage("*collation*Rebuild*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Legacy_files_validate_order_and_unique_equivalence_before_queries(bool equivalentKeys)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                var rows = db.GetCollection("rows");
                foreach (var key in equivalentKeys ? new[] { "a", "A" } : new[] { "-", "_", "a", "A", "ä", "z" })
                    rows.Insert(new BsonDocument { ["_id"] = key });
            }
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            Array.Copy(BitConverter.GetBytes((int)(equivalentKeys ? CompareOptions.IgnoreCase : CompareOptions.None)), 0,
                bytes, EnginePragmas.P_COLLATION_SORT, 4);
            PageChecksum.Write(new BufferSlice(bytes, 0, Constants.PAGE_SIZE));
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }); };
            open.Should().Throw<LiteException>().WithMessage("*collation*Rebuild*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Fact]
        public void Valid_legacy_file_stays_readable_and_read_only_validation_preserves_bytes()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = "one", ["code"] = "first" });
                rows.Insert(new BsonDocument { ["_id"] = "two", ["code"] = "second" });
                rows.EnsureIndex("code");
            }
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            PageChecksum.Write(new BufferSlice(bytes, 0, Constants.PAGE_SIZE));
            File.WriteAllBytes(file.Filename, bytes);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                db.GetCollection("rows").FindById("one")["code"].AsString.Should().Be("first");
                db.GetCollection("rows").Count().Should().Be(2);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
            using (var db = new LiteDatabase(file.Filename)) db.Rebuild(new RebuildOptions { Collation = Collation.Binary });
            BitConverter.ToUInt32(File.ReadAllBytes(file.Filename), EnginePragmas.P_COLLATION_STAMP).Should().Be(CollationFingerprint.Compute(Collation.Binary));
        }

        [Fact]
        public void Recovered_WAL_header_stamp_is_checked_before_admitting_queries()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = "one" });
            }
            LiteDB.Tests.Engine.IndexMigrationFixtures.Rewrite(data, log, null,
                header => header[EnginePragmas.P_COLLATION_STAMP] ^= 1, changeData: false);
            using var copiedData = new MemoryStream(data.ToArray());
            using var changedLog = new MemoryStream(log.ToArray());
            Action open = () => { using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = copiedData, LogStream = changedLog, ReadOnly = true })); };
            open.Should().Throw<LiteException>().WithMessage("*collation*Rebuild*");
        }

        [Fact]
        public void Ordinal_stamp_does_not_depend_on_culture_sort_tables()
        {
            CollationFingerprint.Compute(new Collation("de-DE/Ordinal")).Should().Be(CollationFingerprint.Compute(Collation.Binary));
        }
    }
}
