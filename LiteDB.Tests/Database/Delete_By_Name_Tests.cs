using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Delete_By_Name_Tests
    {
        #region Model

        public class Person
        {
            public int Id { get; set; }
            public string Fullname { get; set; }
        }

        #endregion

        [Fact]
        public void Delete_By_Name()
        {
            using (var f = new TempFile())
            using (var db = new LiteDatabase(f.Filename))
            {
                var col = db.GetCollection<Person>("Person");

                col.Insert(new Person { Fullname = "John" });
                col.Insert(new Person { Fullname = "Doe" });
                col.Insert(new Person { Fullname = "Joana" });
                col.Insert(new Person { Fullname = "Marcus" });

                // lets auto-create index in FullName and delete from a non-pk node
                var del = col.DeleteMany(x => x.Fullname.StartsWith("J"));

                del.Should().Be(2);
            }
        }
    }
}