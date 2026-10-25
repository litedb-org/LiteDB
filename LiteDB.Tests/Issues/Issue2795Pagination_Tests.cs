using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2795Pagination_Tests
    {
        [Fact]
        public void Residual_filter_and_unindexed_order_paginate_after_document_processing()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 30).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = 31 - id, ["keep"] = id % 3 == 0
            }));
            rows.Query().Where("keep = true").Offset(2).Limit(3).ToDocuments()
                .Select(row => row["_id"].AsInt32).Should().Equal(9, 12, 15);
            rows.Query().OrderBy("value").Offset(2).Limit(3).ToDocuments()
                .Select(row => row["_id"].AsInt32).Should().Equal(28, 27, 26);
            rows.Query().Select("COUNT(*)").Offset(27).Limit(3).ToDocuments()
                .Single().Values.Single().AsInt32.Should().Be(3);
        }

        [Fact]
        public void Include_before_filter_keeps_membership_and_include_after_keeps_page_payloads()
        {
            using var db = new LiteDatabase(":memory:");
            var targets = db.GetCollection("targets");
            var rows = db.GetCollection("rows");
            for (var id = 1; id <= 12; id++)
            {
                targets.Insert(new BsonDocument { ["_id"] = id, ["keep"] = id % 2 == 0 });
                rows.Insert(new BsonDocument { ["_id"] = id,
                    ["ref"] = new BsonDocument { ["$id"] = id, ["$ref"] = "targets" } });
            }
            rows.Query().Include("ref").Where("ref.keep = true").Offset(2).Limit(2).ToDocuments()
                .Select(row => row["_id"].AsInt32).Should().Equal(6, 8);
            var page = rows.Query().Include("ref").Offset(2).Limit(2).ToDocuments().ToArray();
            page.Select(row => row["_id"].AsInt32).Should().Equal(3, 4);
            page.Select(row => row["ref"]["keep"].AsBoolean).Should().Equal(false, true);
        }
    }
}
