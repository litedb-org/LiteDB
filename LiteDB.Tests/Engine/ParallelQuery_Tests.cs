using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class ParallelQuery_Tests
    {
        [Fact(Skip = "Must fix parallel query fetch")]
        public void Query_Parallel()
        {
            using(var db = new LiteDatabase(new MemoryStream()))
            {
                var col = db.GetCollection<Person>("person");
                var all = DataGen.Person().ToArray();

                col.Insert(all);

                var bag = new ConcurrentBag<Person>();
                var people = col.FindAll();

                Parallel.ForEach(people, person =>
                //foreach(var person in people)
                {
                    var col2 = db.GetCollection<Person>("person");
                    var exists = col2.Exists(x => x.Id == person.Id);

                    if (exists)
                    {
                        var col3 = db.GetCollection<Person>("person");

                        var item = col3.FindOne(x => x.Id == person.Id);

                        bag.Add(item);
                    }
                });

                all.Length.Should().Be(bag.Count);

            }
        }
    }
}