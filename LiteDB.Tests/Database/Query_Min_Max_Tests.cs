using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Query_Min_Max_Tests
    {
        #region Model

        public class EntityMinMax
        {
            public int Id { get; set; }
            public byte ByteValue { get; set; }
            public int IntValue { get; set; }
            public uint UintValue { get; set; }
            public long LongValue { get; set; }
        }

        #endregion

        [Fact]
        public void Query_Min_Max()
        {
            using (var f = new TempFile())
            using (var db = new LiteDatabase(f.Filename))
            {
                var c = db.GetCollection<EntityMinMax>("col");

                c.Insert(new EntityMinMax { });
                c.Insert(new EntityMinMax
                {
                    ByteValue = 200,
                    IntValue = 443500,
                    LongValue = 443500,
                    UintValue = 443500
                });

                c.EnsureIndex(x => x.ByteValue);
                c.EnsureIndex(x => x.IntValue);
                c.EnsureIndex(x => x.LongValue);
                c.EnsureIndex(x => x.UintValue);

                c.Max(x => x.ByteValue).Should().Be(200);
                c.Max(x => x.IntValue).Should().Be(443500);
                c.Max(x => x.LongValue).Should().Be(443500);
                c.Max(x => x.UintValue).Should().Be(443500);

            }
        }
    }
}