using System;
using System.Linq;
using FluentAssertions;
using Xunit;
#if NET8_0_OR_GREATER
using System.Reflection;
using LiteDB.Engine;
#endif

namespace LiteDB.Tests.Issues
{
    // Enumerating more than 2^31 values takes seconds per run, so the width rule is tested on the value it is applied to.
    public class Issue2932_Tests
    {
        private const long AboveInt32 = (long)int.MaxValue + 7;

        [Theory]
        [InlineData(0L)]
        [InlineData(5L)]
        [InlineData((long)int.MaxValue)]
        public void Counts_that_fit_keep_the_Int32_result_type(long count)
        {
            var value = BsonExpressionMethods.CountValue(count);

            value.Type.Should().Be(BsonType.Int32, "existing SELECT COUNT(...) results must keep their BSON type");
            value.AsInt64.Should().Be(count);
        }

        [Fact]
        public void A_count_above_Int32_is_returned_as_Int64_instead_of_overflowing()
        {
            var value = BsonExpressionMethods.CountValue(AboveInt32);

            value.Type.Should().Be(BsonType.Int64);
            value.AsInt64.Should().Be(AboveInt32);
        }

        [Fact]
        public void COUNT_still_counts_every_value_of_a_lazy_sequence()
        {
            var values = Enumerable.Range(0, 1000).Select(x => new BsonValue(x));

            BsonExpressionMethods.COUNT(values).Should().Be(new BsonValue(1000));

            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 25).Select(x => new BsonDocument { ["_id"] = x }));
            rows.Count().Should().Be(25);
            rows.LongCount().Should().Be(25L);
            db.Execute("SELECT COUNT(*) FROM rows").ToList().Single()["expr"].Type.Should().Be(BsonType.Int32);
        }

#if NET8_0_OR_GREATER
        public class BigCountEngine : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IDisposable.Dispose)) return null;
                if (method.Name == nameof(ILiteEngine.Query)) return new BsonDataReader(new BsonDocument { ["count"] = AboveInt32 });

                throw new InvalidOperationException("Unexpected engine call: " + method.Name);
            }
        }

        [Fact]
        public void Count_tells_the_caller_to_use_LongCount_when_the_result_does_not_fit()
        {
            using var db = new LiteDatabase(DispatchProxy.Create<ILiteEngine, BigCountEngine>());
            var rows = db.GetCollection("rows");

            Action count = () => rows.Count();

            count.Should().Throw<OverflowException>().WithMessage("*LongCount*");
            rows.LongCount().Should().Be(AboveInt32);
        }
#endif
    }
}
