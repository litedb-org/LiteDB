using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class IndexMultiKeyIndex
    {
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

        #endregion

        [Fact]
        public void Index_Multikey_Using_Linq()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
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

                planPhone["index"]["expr"].AsString.Should().Be("$.Phones[*]");

                var docsPhone = queryPhone.ToArray();

                docsPhone.Length.Should().Be(1);

                // doing query over Address
                var queryAddress = col.Query()
                    .Where(x => x.Addresses.Select(a => a.Street).Any(s => s == "Av.3"));

                var planAddress = queryAddress.GetPlan();

                planAddress["index"]["expr"].AsString.Should().Be("MAP($.Addresses[*]=>@.Street)");

                var docsAddress = queryAddress.ToArray();

                docsAddress.Length.Should().Be(2);

                // now, using ALL (do not use INDEX)
                var queryPhoneAll = col.Query()
                    .Where(x => x.Phones.All(p => p == 3));

                var planPhoneAll = queryPhoneAll.GetPlan();

                planPhoneAll["index"]["expr"].AsString.Should().Be("$._id");

                var docsPhoneAll = queryPhoneAll.ToArray();

                docsPhoneAll.Length.Should().Be(0);


            }
        }

    }
}