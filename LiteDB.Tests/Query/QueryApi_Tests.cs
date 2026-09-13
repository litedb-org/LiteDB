using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class QueryApi_Tests : PersonQueryData
    {
        [Fact]
        public void Query_And()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local.Where(x => x.Age == 22 && x.Active == true).ToArray();

            var r1 = collection.Find(Query.And(Query.EQ("Age", 22), Query.EQ("Active", true))).ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }

        [Fact]
        public void Query_And_Same_Field()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local.Where(x => x.Age > 22 && x.Age < 25).ToArray();

            var r1 = collection.Find(Query.And(Query.GT("Age", 22), Query.LT("Age", 25))).ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }
    }
}