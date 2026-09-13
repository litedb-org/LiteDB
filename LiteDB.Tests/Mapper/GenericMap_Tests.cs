using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class GenericMap_Tests
    {
        public class User<T, K>
        {
            public T Id { get; set; }
            public K Name { get; set; }
        }

        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Generic_Map()
        {
            var guid = Guid.NewGuid();
            var today = DateTime.Today;

            var u0 = new User<int, string> {Id = 1, Name = "John"};
            var u1 = new User<double, Guid> {Id = 99.9, Name = guid};
            var u2 = new User<DateTime, string> {Id = today, Name = "Carlos"};
            var u3 = new User<Dictionary<string, object>, string>
            {
                Id = new Dictionary<string, object> {["f"] = "user1", ["n"] = 4},
                Name = "Complex User"
            };
            var u4 = new User<ISet<object>, string>
            {
                Id = new HashSet<object> { 1, 3, "user2" },
                Name = "User"
            };

            var d0 = _mapper.ToDocument(u0.GetType(), u0);
            var d1 = _mapper.ToDocument(u1.GetType(), u1);
            var d2 = _mapper.ToDocument(u2.GetType(), u2);
            var d3 = _mapper.ToDocument(u3.GetType(), u3);
            var d4 = _mapper.ToDocument(u4.GetType(), u4);

            d0["_id"].AsInt32.Should().Be(1);
            d0["Name"].AsString.Should().Be("John");

            d1["_id"].AsDouble.Should().Be(99.9);
            d1["Name"].AsGuid.Should().Be(guid);

            d2["_id"].AsDateTime.Should().Be(today);
            d2["Name"].AsString.Should().Be("Carlos");

            d3["_id"]["f"].AsString.Should().Be("user1");
            d3["_id"]["n"].AsInt32.Should().Be(4);
            d3["Name"].AsString.Should().Be("Complex User");

            d4["_id"][0].AsInt32.Should().Be(1);
            d4["_id"][1].AsInt32.Should().Be(3);
            d4["_id"][2].AsString.Should().Be("user2");
            d4["Name"].AsString.Should().Be("User");
        }
    }
}