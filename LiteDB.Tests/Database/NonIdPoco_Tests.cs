using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class MissingIdDocTest
    {
        #region Model

        public class MissingIdDoc
        {
            public string Name { get; set; }
            public int Age { get; set; }
        }

        #endregion

        [Fact]
        public void MissingIdDoc_Test()
        {
            using (var db = DatabaseFactory.Create())
            {
                var col = db.GetCollection<MissingIdDoc>("col");

                var p = new MissingIdDoc { Name = "John", Age = 39 };

                // ObjectID will be generated 
                var id = col.Insert(p);

                p.Age = 41;

                col.Update(id, p);

                var r = col.FindById(id);

                r.Name.Should().Be(p.Name);
            }
        }
    }
}