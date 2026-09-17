using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    /// <summary>
    /// A BsonDocument collection is part of the trimming and Native AOT contract, so a LINQ expression on it must
    /// never hand a captured value to reflection-based model mapping.
    /// </summary>
    public class DocumentLinqConstants_Tests
    {
        private sealed class ApplicationObject
        {
            public int Minimum { get; set; }
        }

        private enum Level
        {
            Low,
            High
        }

        private static ILiteCollection<BsonDocument> CreateCollection(LiteDatabase database)
        {
            var collection = database.GetCollection("items");

            collection.Insert(new BsonDocument { ["_id"] = 1, ["score"] = 10, ["level"] = "Low", ["meta"] = new BsonDocument { ["a"] = 1 } });
            collection.Insert(new BsonDocument { ["_id"] = 2, ["score"] = 20, ["level"] = "High", ["meta"] = new BsonDocument { ["a"] = 2 } });

            return collection;
        }

        [Fact]
        public void Captured_Bson_Native_Values_Are_Serialized()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var collection = CreateCollection(database);

            var minimum = 15;
            var level = Level.High;
            var scores = new List<int> { 10, 99 };
            BsonValue meta = new BsonDocument { ["a"] = 2 };
            var options = new ApplicationObject { Minimum = 15 };

            collection.Count(x => x["score"] > minimum).Should().Be(1);
            collection.Count(x => x["level"] == level.ToString()).Should().Be(1);
            collection.Count(x => scores.Contains(x["score"].AsInt32)).Should().Be(1);
            collection.Count(x => x["meta"] == meta).Should().Be(1);

            // the documented way to compare against values taken from application objects
            var wanted = new List<int> { 2, 7 };
            collection.Count(x => wanted.Contains(x["meta"]["a"].AsInt32)).Should().Be(1);

            // reading a scalar member of a captured object is evaluated before serialization
            collection.Count(x => x["score"] > options.Minimum).Should().Be(1);
        }

        [Fact]
        public void Captured_Application_Object_Is_Rejected_Instead_Of_Runtime_Mapped()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var collection = CreateCollection(database);
            var captured = new object[] { new ApplicationObject { Minimum = 1 } };

            Action query = () => collection.Count(x => captured.Contains(x["meta"]));

            query.Should().Throw<NotSupportedException>().WithMessage("*ApplicationObject*");
        }
    }
}
