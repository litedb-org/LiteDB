using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
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
            using (var file = new TempFile())
            using (var db = new LiteDatabase(file.Filename))
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