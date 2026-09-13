using FluentAssertions;
using LiteDB.Engine;
using System;
using System.IO;
using System.Linq;

using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Rebuild_Tests
    {
        [Fact]
        public void Rebuild_After_DropCollection()
        {
            using (var file = new TempFile())
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<Zip>("zip");

                col.Insert(DataGen.Zip());

                db.DropCollection("zip");

                db.Checkpoint();

                // full disk usage
                var size = file.Size;

                var r = db.Rebuild();

                // only header page
                Assert.Equal(8192, size - r);
            }
        }

        [Fact]
        public void Rebuild_Large_Files()
        {
            // do some tests
            void DoTest(ILiteDatabase db, ILiteCollection<Zip> col)
            {
                Assert.Equal(1, col.Count());
                Assert.Equal(99, db.UserVersion);
            };

            using (var file = new TempFile())
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var col = db.GetCollection<Zip>();

                    db.UserVersion = 99;

                    col.EnsureIndex("city", false);

                    var inserted = col.Insert(DataGen.Zip()); // 29.353 docs
                    var deleted = col.DeleteMany(x => x.Id != "01001"); // delete 29.352 docs

                    Assert.Equal(29353, inserted);
                    Assert.Equal(29352, deleted);

                    Assert.Equal(1, col.Count());

                    // must checkpoint
                    db.Checkpoint();

                    // file still large than 5mb (even with only 1 document)
                    Assert.True(file.Size > 5 * 1024 * 1024);

                    // reduce datafile
                    var reduced = db.Rebuild();

                    // now file are small than 50kb
                    Assert.True(file.Size < 50 * 1024);

                    DoTest(db, col);
                }

                // re-open and rebuild again
                using (var db = new LiteDatabase(file.Filename))
                {
                    var col = db.GetCollection<Zip>();

                    DoTest(db, col);

                    db.Rebuild();

                    DoTest(db, col);
                }
            }
        }

        [Fact (Skip = "Not supported yet")]
        public void Rebuild_Change_Culture_Error()
        {
            using (var file = new TempFile())
            using (var db = new LiteDatabase(file.Filename))
            {
                // remove string comparer ignore case
                db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/None") });

                // insert 2 documents with different ID in case sensitive
                db.GetCollection("col1").Insert(new BsonDocument[]
                {
                    new BsonDocument { ["_id"] = "ana" },
                    new BsonDocument { ["_id"] = "ANA" }
                });

                // migrate to ignorecase
                db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase"), IncludeErrorReport = true });

                // check for rebuild errors
                db.GetCollection("_rebuild_errors").Count().Should().BeGreaterThan(0);

                // test if current pragma still with collation none
                db.Pragma(Pragmas.COLLATION).AsString.Should().Be("en-US/None");
            }
        }
    }
}

