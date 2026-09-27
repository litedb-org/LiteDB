using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationFullDatabase_Tests
    {
        /// <summary>
        /// Page allocation checks the length before adding a page, so a database that
        /// reached LIMIT_SIZE through inserts is one page larger than the limit (the
        /// dev engine behaves identically). Such a file has only a scalar primary key,
        /// which the compatibility contract says migrates at the existing limit.
        /// </summary>
        [Fact]
        public void Scalar_only_database_that_reached_its_limit_migrates_without_extra_options()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 0, ["payload"] = "x" });
                db.Checkpoint();
                db.LimitSize = new FileInfo(file.Filename).Length + 4 * Constants.PAGE_SIZE;
                Action fill = () =>
                {
                    for (var i = 1; i < 1000; i++)
                        rows.Insert(new BsonDocument { ["_id"] = i, ["payload"] = new string('x', 3000) });
                };
                fill.Should().Throw<LiteException>().WithMessage("*exceeds limit*");
                db.Checkpoint();
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 8;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });

            Action open = () =>
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Count().Should().BeGreaterThan(1);
            };
            open.Should().NotThrow("only the scalar primary key needs reordering, in place");
        }
    }
}
