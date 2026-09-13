using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class Records_Tests
    {
        public record User(int Id, string Name);


        [Fact]
        public void Record_Simple_Mapper()
        {
            var mapper = new BsonMapper();

            var user = new User(1, "John");
            var doc = mapper.ToDocument(user);
            var user2 = mapper.ToObject<User>(doc);

            Assert.Equal(user.Id, user2.Id);
            Assert.Equal(user.Name, user2.Name);
        }
    }
}