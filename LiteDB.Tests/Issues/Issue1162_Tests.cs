using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1162_Tests
    {
        [Fact]
        public void Dynamic_expando_list_keeps_keys_values_and_distinct_rows()
        {
            dynamic first = new ExpandoObject();
            first.Test = "Yes";
            first.Value = 123;
            dynamic second = new ExpandoObject();
            second.Test = "No";
            second.Value = 456;
            var mapper = new BsonMapper();
            var input = new List<dynamic> { first, second };
            var documents = input.Select(x => (BsonDocument)mapper.ToDocument(x)).ToList();
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").InsertBulk(documents).Should().Be(2);
            var rows = db.GetCollection("rows").FindAll().OrderBy(x => x["Value"].AsInt32).ToArray();
            rows.Select(x => x["Test"].AsString).Should().Equal("Yes", "No");
            rows.Select(x => x["Value"].AsInt32).Should().Equal(123, 456);
            ((IDictionary<string, object>)first)["Value"].Should().Be(123);
        }
    }
}
