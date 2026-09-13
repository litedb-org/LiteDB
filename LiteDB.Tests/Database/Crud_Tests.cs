using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Crud_Tests
    {
        #region Model 

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        #endregion

        [Fact]
        public void Insert_With_AutoId()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var users = db.GetCollection<User>("users");

                var u1 = new User { Name = "John" };
                var u2 = new User { Name = "Zarlos" };
                var u3 = new User { Name = "Ana" };

                // insert ienumerable
                users.Insert(new User[] { u1, u2 });

                users.Insert(u3);

                // test auto-id
                u1.Id.Should().Be(1);
                u2.Id.Should().Be(2);
                u3.Id.Should().Be(3);

                // adding without autoId
                var u4 = new User { Id = 20, Name = "Marco" };

                users.Insert(u4);

                // adding more auto id after fixed id
                var u5 = new User { Name = "Julio" };

                users.Insert(u5);

                u5.Id.Should().Be(21);
            }
        }

        [Fact]
        public void Delete_Many()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var users = db.GetCollection<User>("users");

                var u1 = new User { Id = 1, Name = "John" };
                var u2 = new User { Id = 2, Name = "Zarlos" };
                var u3 = new User { Id = 3, Name = "Ana" };

                users.Insert(new User[] { u1, u2, u3 });

                var ids = new int[] { 1, 2, 3 };

                var docs = users.Query().Where(x => ids.Contains(x.Id)).ToArray();

                users.DeleteMany(x => ids.Contains(x.Id));

                users.Count().Should().Be(0);

            }
        }
    }
}