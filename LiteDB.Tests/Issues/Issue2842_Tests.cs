using System;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2842_Tests
    {
        [Fact]
        public void Size_limit_is_classified_and_failed_write_does_not_change_committed_rows()
        {
            using var file = new TempFile();
            var committed = 0;
            Exception failure = null;
            using (var db = new LiteDatabase(file.Filename))
            {
                db.LimitSize = 256 * 1024;
                var col = db.GetCollection("rows");
                for (var id = 1; id <= 1000; id++)
                {
                    failure = Record.Exception(() => col.Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('x', 2000) }));
                    if (failure != null) break;
                    committed++;
                }
            }
            using (var db = new LiteDatabase(file.Filename))
            using (new AssertionScope())
            {
                committed.Should().BeGreaterThan(0).And.BeLessThan(1000);
                failure.Should().BeOfType<LiteException>();
                if (failure is LiteException lite) lite.ErrorCode.Should().Be(LiteException.FILE_SIZE_EXCEEDED);
                var col = db.GetCollection("rows");
                col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, committed));
                col.FindAll().Should().OnlyContain(x => x["text"].AsString == new string('x', 2000));
                ((object)col.FindById(committed + 1)).Should().BeNull();
                db.LimitSize = 1024 * 1024;
                col.Insert(new BsonDocument { ["_id"] = committed + 1, ["text"] = "after failure" });
                col.FindById(committed + 1)["text"].AsString.Should().Be("after failure");
            }
        }
    }
}
