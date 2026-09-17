using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798_Tests
    {
        public interface IRow { string Key { get; set; } int Value { get; set; } }
        public class Row : IRow
        {
            [BsonId] public string Key { get; set; }
            public int Value { get; set; }
        }

        public interface IDatedRow
        {
            DateTime Time { get; set; }
            int Marker { get; set; }
        }

        public class DatedRow : IDatedRow
        {
            [BsonId]
            public DateTime Time { get; set; }

            public int Marker { get; set; }
        }

        [Fact]
        public void Interface_queries_and_deletes_use_the_same_id_mapping_as_insert()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<IRow>("rows");
            col.Insert(new Row { Key = "alpha", Value = 11 });
            col.Insert(new Row { Key = "beta", Value = 22 });
            col.FindById("alpha").Value.Should().Be(11);
            db.GetCollection("rows").FindById("alpha")["_id"].AsString.Should().Be("alpha");
            col.Find(x => x.Key == "alpha").Select(x => x.Value).Should().Equal(11);
            col.Query().Where(x => x.Key == "beta").ToArray().Select(x => x.Value).Should().Equal(22);
            col.DeleteMany(x => x.Key == "alpha").Should().Be(1);
            col.FindAll().Select(x => x.Key).Should().Equal("beta");
            db.GetCollection("rows").FindAll().Select(x => x["Value"].AsInt32).Should().Equal(22);
        }

        [Fact]
        public void Interface_DateTime_id_range_uses_the_concrete_id_mapping_for_query_and_delete()
        {
            var old = new DateTime(1985, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            var middle = new DateTime(2020, 3, 4, 0, 0, 0, DateTimeKind.Utc);
            var recent = new DateTime(2035, 5, 6, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var throughInterface = db.GetCollection<IDatedRow>("dated");
            throughInterface.Insert(new DatedRow { Time = old, Marker = 1 });
            throughInterface.Insert(new DatedRow { Time = middle, Marker = 2 });
            throughInterface.Insert(new DatedRow { Time = recent, Marker = 3 });

            var raw = db.GetCollection("dated").FindAll().OrderBy(x => x["_id"]).ToArray();
            raw.Select(x => x["_id"].AsDateTime.ToUniversalTime()).Should().Equal(old, middle, recent);
            raw.Select(x => x["Marker"].AsInt32).Should().Equal(1, 2, 3);
            throughInterface.FindById(middle).Marker.Should().Be(2);

            var concreteIds = db.GetCollection<DatedRow>("dated")
                .Find(x => x.Time > cutoff)
                .OrderBy(x => x.Time)
                .Select(x => x.Marker);
            concreteIds.Should().Equal(new[] { 2, 3 },
                "the concrete mapping is the independent query-path control");

            throughInterface.Query().Where(x => x.Time > cutoff).ToArray()
                .OrderBy(x => x.Time).Select(x => x.Marker).Should().Equal(2, 3);
            throughInterface.DeleteMany(x => x.Time > middle).Should().Be(1);
            throughInterface.FindAll().OrderBy(x => x.Time).Select(x => x.Marker).Should().Equal(1, 2);
            db.GetCollection("dated").FindAll().Select(x => x["Marker"].AsInt32).OrderBy(x => x)
                .Should().Equal(1, 2);
        }
    }
}
