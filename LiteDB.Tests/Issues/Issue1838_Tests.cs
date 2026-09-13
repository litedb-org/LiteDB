using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;
using LiteDB.Tests.Utils;

namespace LiteDB.Tests.Issues
{
    public class Issue1838_Tests
    {
        [Fact]
        public void Find_ByDatetime_Offset()
        {
            using var db = DatabaseFactory.Create();
            var collection = db.GetCollection<TestType>(nameof(TestType));

            // sample data
            collection.Insert(new TestType()
            {
                Foo = "abc",
                Timestamp = DateTimeOffset.UtcNow,
            });
            collection.Insert(new TestType()
            {
                Foo = "def",
                Timestamp = DateTimeOffset.UtcNow,
            });

            // filter from 1 hour in the past to 1 hour in the future
            var timeRange = TimeSpan.FromHours(2);

            var result = collection // throws exception
                .Find(x => x.Timestamp > (DateTimeOffset.UtcNow - timeRange))
                .ToList();

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
        }

        public class TestType
        {
            [BsonId]
            public int Id { get; set; }
            [BsonField]
            public string Foo { get; set; }
            [BsonField]
            public DateTimeOffset Timestamp { get; set; }
        }
    }
}
