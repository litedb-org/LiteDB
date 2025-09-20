using FluentAssertions;
using LiteDB.Engine;
using System;
using System.Collections.Generic;
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

                col.Insert(CreateSyntheticZipData(200, SurvivorId));

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

                    const int documentCount = 200;

                    var inserted = col.Insert(CreateSyntheticZipData(documentCount, SurvivorId));
                    var deleted = col.DeleteMany(x => x.Id != SurvivorId);

                    Assert.Equal(documentCount, inserted);
                    Assert.Equal(documentCount - 1, deleted);

                    Assert.Equal(1, col.Count());

                    // must checkpoint
                    db.Checkpoint();

                    // file still larger than 1 MB (even with only 1 document)
                    Assert.True(file.Size > 1 * 1024 * 1024);

                    // reduce datafile
                    var reduced = db.Rebuild();

                    // now file should be small again
                    Assert.True(file.Size < 256 * 1024);

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

        private const string SurvivorId = "01001";

        private static IEnumerable<Zip> CreateSyntheticZipData(int totalCount, string survivingId)
        {
            if (totalCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(totalCount));
            }

            const int payloadLength = 32 * 1024; // 32 KB payload to force file growth

            for (var i = 0; i < totalCount; i++)
            {
                var id = (20000 + i).ToString("00000");

                if (!string.IsNullOrEmpty(survivingId) && i == 0)
                {
                    id = survivingId;
                }

                var payload = new byte[payloadLength];
                Array.Fill(payload, (byte)(i % 256));

                yield return new Zip
                {
                    Id = id,
                    City = $"City {i:D4}",
                    Loc = new[] { (double)i, (double)i + 0.5 },
                    State = "ST",
                    Payload = payload
                };
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

