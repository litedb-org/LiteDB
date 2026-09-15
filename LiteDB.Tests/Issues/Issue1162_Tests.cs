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
            var first = new ExpandoObject();
            var firstValues = (IDictionary<string, object>)first;
            firstValues["Test"] = "Yes";
            firstValues["Value"] = 123;
            var second = new ExpandoObject();
            var secondValues = (IDictionary<string, object>)second;
            secondValues["Test"] = "No";
            secondValues["Value"] = 456;
            var mapper = new BsonMapper();
            var input = new List<ExpandoObject> { first, second };
            var documents = input.Select(x => mapper.ToDocument(x.GetType(), x)).ToList();
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").InsertBulk(documents).Should().Be(2);
            var rows = db.GetCollection("rows").FindAll().OrderBy(x => x["Value"].AsInt32).ToArray();
            rows.Select(x => x["Test"].AsString).Should().Equal("Yes", "No");
            rows.Select(x => x["Value"].AsInt32).Should().Equal(123, 456);
            firstValues["Value"].Should().Be(123);
        }
    }
}
