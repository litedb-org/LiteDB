using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2322_Tests
    {
        public enum Kind { A, B }
        public interface IRow<K> where K : Enum
        {
            int Id { get; }
            K Kind { get; }
        }
        public class Row : IRow<Kind>
        {
            public int Id { get; set; }
            public Kind Kind { get; set; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Generic_enum_Equals_matches_original_rows_in_both_storage_modes(bool asInteger)
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper { EnumAsInteger = asInteger });
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1, Kind = Kind.A }, new Row { Id = 2, Kind = Kind.B }, new Row { Id = 3, Kind = Kind.A } });
            Find<Row, Kind>(col, Kind.A).Should().Equal(1, 3);
            Find<Row, Kind>(col, Kind.B).Should().Equal(2);
            Find<Row, Kind>(col, (Kind)42).Should().BeEmpty();
            col.FindAll().OrderBy(x => x.Id).Select(x => x.Kind).Should().Equal(Kind.A, Kind.B, Kind.A);
        }

        private static int[] Find<T, K>(ILiteCollection<T> col, K kind) where T : IRow<K> where K : Enum
        {
            return col.Find(x => x.Kind.Equals(kind)).Select(x => x.Id).OrderBy(x => x).ToArray();
        }
    }
}
