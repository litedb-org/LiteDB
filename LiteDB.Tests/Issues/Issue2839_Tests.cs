#if NET8_0_OR_GREATER
using System;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2839_Tests
    {
        // Supply an actual Int64 engine result at the public engine boundary, avoiding
        // billions of inserts. Do not mock LongCount or its conversion logic.
        public class CountEngine : DispatchProxy
        {
            public long CountValue;
            public int Queries;
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IDisposable.Dispose)) return null;
                if (method.Name == nameof(ILiteEngine.Query))
                {
                    ((string)args[0]).Should().Be("rows");
                    var query = (Query)args[1];
                    query.Select.Source.Should().Contain("COUNT");
                    Queries++;
                    return new BsonDataReader(new BsonDocument { ["count"] = CountValue });
                }
                throw new InvalidOperationException("Unexpected engine call: " + method.Name);
            }
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(2147483647L)]
        [InlineData(2147483648L)]
        [InlineData(4294967303L)]
        public void LongCount_Query_preserves_full_width_engine_result(long expected)
        {
            var engine = DispatchProxy.Create<ILiteEngine, CountEngine>();
            var fake = (CountEngine)(object)engine;
            fake.CountValue = expected;
            using var db = new LiteDatabase(engine);
            var col = db.GetCollection("rows");
            col.LongCount().Should().Be(expected);
            col.LongCount(Query.All()).Should().Be(expected);
            fake.Queries.Should().Be(2);
        }
    }
}
#endif
