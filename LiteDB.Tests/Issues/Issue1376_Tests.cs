using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1376_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public Dictionary<string, object> Data { get; set; }
        }

        [Fact]
        public void ContainsKey_distinguishes_absent_null_and_non_null_values()
        {
            var rows = new[]
            {
                new Row { Id = 1, Data = new Dictionary<string, object> { ["key"] = 42 } },
                new Row { Id = 2, Data = new Dictionary<string, object> { ["key"] = null } },
                new Row { Id = 3, Data = new Dictionary<string, object> { ["other"] = 42 } },
                new Row { Id = 4, Data = new Dictionary<string, object>() }
            };
            using var db = new LiteDatabase(":memory:", new BsonMapper { SerializeNullValues = true });
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var key in new[] { "key", "other", "missing" })
            {
                col.Find(x => x.Data.ContainsKey(key)).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(rows.Where(x => x.Data.ContainsKey(key)).Select(x => x.Id));
                col.Find(x => !x.Data.ContainsKey(key)).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(rows.Where(x => !x.Data.ContainsKey(key)).Select(x => x.Id));
            }
        }
    }
}
