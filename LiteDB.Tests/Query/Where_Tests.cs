using FluentAssertions;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Where_Tests : PersonQueryData
    {
        class Entity
        {
            public string Name { get; set; }
            public int Size { get; set; }
        }

        [Fact]
        public void Query_Where_With_Parameter()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local
                .Where(x => x.Address.State == "FL")
                .ToArray();

            var r1 = collection.Query()
                .Where(x => x.Address.State == "FL")
                .ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }

        [Fact]
        public void Query_Multi_Where_With_Like()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local
                .Where(x => x.Age >= 10 && x.Age <= 40)
                .Where(x => x.Name.StartsWith("Ge"))
                .ToArray();

            var r1 = collection.Query()
                .Where(x => x.Age >= 10 && x.Age <= 40)
                .Where(x => x.Name.StartsWith("Ge"))
                .ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }

        [Fact]
        public void Query_Single_Where_With_And()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local
                .Where(x => x.Age == 25 && x.Active)
                .ToArray();

            var r1 = collection.Query()
                .Where("age = 25 AND active = true")
                .ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }

        [Fact]
        public void Query_Single_Where_With_Or_And_In()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var r0 = local
                .Where(x => x.Age == 25 || x.Age == 26 || x.Age == 27)
                .ToArray();

            var r1 = collection.Query()
                .Where("age = 25 OR age = 26 OR age = 27")
                .ToArray();

            var r2 = collection.Query()
                .Where("age IN [25, 26, 27]")
                .ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
            AssertEx.ArrayEqual(r1, r2, true);
        }

        [Fact]
        public void Query_With_Array_Ids()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            var ids = new int[] { 1, 2, 3 };

            var r0 = local
                .Where(x => ids.Contains(x.Id))
                .ToArray();

            var r1 = collection.Query()
                .Where(x => ids.Contains(x.Id))
                .ToArray();

            AssertEx.ArrayEqual(r0, r1, true);
        }
    }
}