using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2801_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Object_constructor_collections_have_usable_views_and_persist_mutations(bool dictionary)
        {
            object input = dictionary
                ? (object)new Dictionary<string, object> { ["value"] = 17, ["nested"] = new[] { "a", "b" } }
                : new[] { "a", "b" };
            var value = new BsonValue(input);
            BsonValue expected;
            if (dictionary)
            {
                ((object)value.AsDocument).Should().NotBeNull();
                value.AsDocument["value"] = 23;
                expected = new BsonDocument { ["value"] = 23, ["nested"] = new BsonArray("a", "b") };
            }
            else
            {
                ((object)value.AsArray).Should().NotBeNull();
                value.AsArray.Add("c");
                expected = new BsonArray("a", "b", "c");
            }
            value.CompareTo(expected).Should().Be(0);
            expected.CompareTo(value).Should().Be(0);
            JsonSerializer.Deserialize(value.ToString()).Should().Be(expected);
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Upsert(new BsonDocument { ["_id"] = 1, ["Value"] = value }).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").FindById(1)["Value"].Should().Be(expected);
                db.GetCollection("rows").Count().Should().Be(1);
            }
        }
    }
}
