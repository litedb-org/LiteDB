using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2376_Tests
    {
        public enum ByteKind : byte { Me, You, Us = 255 }
        public class Row
        {
            public int Id { get; set; }
            public ByteKind[] Values { get; set; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Byte_enum_arrays_survive_insert_update_and_reopen(bool asInteger)
        {
            using var file = new TempFile();
            var mapper = new BsonMapper { EnumAsInteger = asInteger };
            using (var db = new LiteDatabase(file.Filename, mapper))
            {
                var col = db.GetCollection<Row>("rows");
                col.Insert(new Row { Id = 1, Values = new[] { ByteKind.Me, ByteKind.Us, ByteKind.You } });
                col.Insert(new Row { Id = 2, Values = new ByteKind[0] });
                col.FindById(1).Values.Should().Equal(ByteKind.Me, ByteKind.Us, ByteKind.You);
                col.Update(new Row { Id = 1, Values = new[] { ByteKind.Us, ByteKind.You } }).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper { EnumAsInteger = asInteger }))
            {
                db.GetCollection<Row>("rows").FindById(1).Values.Should().Equal(ByteKind.Us, ByteKind.You);
                db.GetCollection<Row>("rows").FindById(2).Values.Should().BeEmpty();
                var raw = db.GetCollection("rows").FindById(1)["Values"];
                // CLR runtimes may expose byte-backed enum arrays as byte[]. Either storage
                // representation is valid, but verify its contents without the mapper's decoder.
                if (raw.IsBinary)
                {
                    raw.AsBinary.Should().Equal(255, 1);
                }
                else
                {
                    raw.IsArray.Should().BeTrue();
                    raw.AsArray.Count.Should().Be(2);
                    raw.AsArray[0].Should().Be(asInteger ? new BsonValue(255) : new BsonValue("Us"));
                    raw.AsArray[1].Should().Be(asInteger ? new BsonValue(1) : new BsonValue("You"));
                }
            }
        }
    }
}
