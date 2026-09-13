using System;

using Xunit;

namespace LiteDB.Tests.Issues;

// issue 2265
public class Issue2265_Tests
{
    public class Weights
    {
        public int Id { get; set; } = 0;

        // comment out [BsonRef] and the the test works
        [BsonRef("weights")]
        public Weights[] Parents { get; set; }

        public Weights(int id, Weights[] parents)
        {
            Id = id;
            Parents = parents;
        }

        public Weights()
        {
            Id = 0;
            Parents = Array.Empty<Weights>();
        }
    }

    [Fact]
    public void Test()
    {
        using (var db = new LiteDatabase(":memory:"))
        {
            var c = db.GetCollection<Weights>("weights");
            Weights? w = c.FindOne(x => true);
            if (w == null)
            {
                w = new Weights();
                c.Insert(w);
            }

            //return w;
        }
    }
}