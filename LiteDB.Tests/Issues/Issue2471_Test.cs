using FluentAssertions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2471_Test
{
    [Fact]
    public void TestFragmentDB_FindByIDException()
    {
        using var db = new LiteDatabase(":memory:");
        var collection = db.GetCollection<object>("fragtest");

        var fragment = new object { };
        var id = collection.Insert(fragment);

        id.Should().BeGreaterThan(0);

        var frag2 = collection.FindById(id);
        frag2.Should().NotBeNull();

        Action act = () => db.Checkpoint();

        act.Should().NotThrow();
    }

    [Fact]
    public void MultipleReadCleansUpTransaction()
    {
        using var database = new LiteDatabase(":memory:");

        var collection = database.GetCollection("test");
        collection.Insert(new BsonDocument { ["_id"] = 1 });

        for (int i = 0; i < 500; i++)
        {
            collection.FindById(1);
        }
    }

    #region Model

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int[] Phones { get; set; }
        public List<Address> Addresses { get; set; }
    }

    public class Address
    {
        public string Street { get; set; }
    }

    #endregion Model

    // Copied from IndexMultiKeyIndex, but this time we ensure that the lock is released by calling  db.Checkpoint()
    [Fact]
    public void Ensure_Query_GetPlan_Releases_Lock()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection<User>();

        col.Insert(new User { Name = "John Doe", Phones = new int[] { 1, 3, 5 }, Addresses = new List<Address> { new Address { Street = "Av.1" }, new Address { Street = "Av.3" } } });
        col.Insert(new User { Name = "Joana Mark", Phones = new int[] { 1, 4 }, Addresses = new List<Address> { new Address { Street = "Av.3" } } });

        // create indexes
        col.EnsureIndex(x => x.Phones);
        col.EnsureIndex(x => x.Addresses.Select(z => z.Street));

        // testing indexes expressions
        var indexes = db.GetCollection("$indexes").FindAll().ToArray();

        indexes[1]["expression"].AsString.Should().Be("$.Phones[*]");
        indexes[2]["expression"].AsString.Should().Be("MAP($.Addresses[*]=>@.Street)");

        // doing Phone query
        var queryPhone = col.Query()
            .Where(x => x.Phones.Contains(3));

        var planPhone = queryPhone.GetPlan();

        Action act = () => db.Checkpoint();

        act.Should().NotThrow();
    }
}