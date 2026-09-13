using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class CustomInterface_Tests
    {
        public class User
        {
            private List<string> strings = new List<string>();
            public int Id { get; set; }
            public IReadOnlyList<string> Strings
            {
                get => strings;
                set => strings = new List<string>(value);
            }
        }

        [Fact]
        public void Custom_Interface_Implements_IEnumerable()
        {
            var mapper = new BsonMapper();

            var user = new User { Id = 1, Strings = new List<string> { "aaa", "bbb" } };
            var doc = mapper.ToDocument(user);
            var user2 = mapper.ToObject<User>(doc);

            Assert.Equal(user.Id, user2.Id);
            Assert.Equal(user.Strings.Count, user2.Strings.Count);
        }
    }
}