using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2357MissingUpdate_Tests
    {
        [Fact]
        public void On_update_of_a_missing_id_writes_nothing_and_therefore_rejects_nothing()
        {
            using var db = Issue2357InvalidTime_Tests.Open(reject: true);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = Issue2357InvalidTime_Tests.Gap.AddHours(-2) });

            rows.Update(new BsonDocument { ["_id"] = 99, ["date"] = Issue2357InvalidTime_Tests.Gap }).Should().BeFalse();
            rows.Count().Should().Be(1);
        }

        [Fact]
        public void On_update_in_a_missing_collection_rejects_nothing()
        {
            using var db = Issue2357InvalidTime_Tests.Open(reject: true);

            db.GetCollection("absent").Update(new BsonDocument { ["_id"] = 1, ["date"] = Issue2357InvalidTime_Tests.Gap })
                .Should().BeFalse();
        }
    }
}
