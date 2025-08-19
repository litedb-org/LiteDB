using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class FindAll_Tests
    {
        #region Model

        public class Person
        {
            public int Id { get; set; }
            public string Fullname { get; set; }
        }

        #endregion

        [Fact]
        public void FindAll()
        {
            using (var f = new TempFile())
            {
                using (var db = new LiteDatabase(f.Filename))
                {
                    var col = db.GetCollection<Person>("Person");

                    col.Insert(new Person { Fullname = "John" });
                    col.Insert(new Person { Fullname = "Doe" });
                    col.Insert(new Person { Fullname = "Joana" });
                    col.Insert(new Person { Fullname = "Marcus" });
                }
                // close datafile

                using (var db = new LiteDatabase(f.Filename))
                {
                    var p = db.GetCollection<Person>("Person").Find(Query.All("Fullname", Query.Ascending));

                    p.Count().Should().Be(4);
                }
            }

        }
    }
}