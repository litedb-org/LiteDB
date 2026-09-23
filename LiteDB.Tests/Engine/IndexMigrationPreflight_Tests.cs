using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationPreflight_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Undersized_stored_limit_rejects_only_migrations_that_grow_the_file(bool computedIndex)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 32).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["name"] = "N" + i, ["payload"] = new string('x', 2000)
                }));
                if (computedIndex) rows.EnsureIndex("lower", "LOWER($.name)");
            }
            // Released engines open a file above its LIMIT_SIZE and only reject growth.
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                MarkLegacy(header);
                Array.Copy(BitConverter.GetBytes(4L * Constants.PAGE_SIZE), 0, header, EnginePragmas.P_LIMIT_SIZE, 8);
            });
            var before = File.ReadAllBytes(file.Filename);
            Action open = () => { using var db = new LiteDatabase(file.Filename); db.GetCollection("rows").Count().Should().Be(32); };
            if (!computedIndex)
            {
                open.Should().NotThrow("the scalar primary key reorders in place without new pages");
                using var db = new LiteDatabase(file.Filename);
                db.LimitSize.Should().Be(4L * Constants.PAGE_SIZE, "migration without an explicit budget keeps the stored limit");
                db.GetCollection("rows").FindById(17)["_id"].AsInt32.Should().Be(17);
                return;
            }
            open.Should().Throw<LiteException>().WithMessage("*capacity*Retry with*index migration limit size*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Invalid_regenerated_keys_are_rejected_before_promotion(bool unique)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["values"] = new BsonArray { 1, new string('x', 1024) } });
                rows.EnsureIndex("values", "$.values[0]", unique);
            }
            // Model a legacy index whose stored keys omitted an oversized value.
            var bytes = File.ReadAllBytes(file.Filename);
            var expression = Encoding.UTF8.GetBytes("$.values[0]");
            var changed = 0;
            for (var offset = 0; offset <= bytes.Length - expression.Length; offset++)
            {
                var match = true;
                for (var i = 0; i < expression.Length && match; i++) match = bytes[offset + i] == expression[i];
                if (!match) continue;
                bytes[offset + expression.Length - 2] = (byte)'*';
                changed++;
            }
            changed.Should().Be(1);
            MarkLegacy(bytes);
            using (var data = LiteDB.Internals.ChecksumTestFiles.Copy(bytes))
            using (var log = new MemoryStream())
            {
                LiteDB.Internals.ChecksumTestFiles.MakeLegacy(data, log, null);
                bytes = data.ToArray();
            }
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(file.Filename); };
            open.Should().Throw<LiteException>().WithMessage("*Invalid key while migrating index values*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        private static void MarkLegacy(byte[] header)
        {
            header[HeaderPage.P_FILE_VERSION] = 8;
            header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
            Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
        }
    }
}
