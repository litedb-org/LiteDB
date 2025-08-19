using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1695_Tests
    {
        public class StateModel
        {
            [BsonId]
            public ObjectId Id { get; set; }
        }

        [Fact]
        public void ICollection_Parameter_Test()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<StateModel>("col");

            ICollection<ObjectId> ids = new List<ObjectId>();
            for (var i = 1; i <= 10; i++)
                ids.Add(col.Insert(new StateModel()));

            var items = col.Query()
                .Where(x => ids.Contains(x.Id))
                .ToList();

            items.Should().HaveCount(10);
        }
    }
}