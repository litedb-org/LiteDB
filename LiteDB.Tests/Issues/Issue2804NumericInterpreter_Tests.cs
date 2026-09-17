using System;
using System.Linq.Expressions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2804NumericInterpreter_Tests
    {
        private enum Example : ushort { Value = 42 }

        [Fact]
        public void Forced_numeric_conversions_match_compiled_CLR_results_and_errors()
        {
            var values = new object[]
            {
                (sbyte)-1, (byte)255, (short)-32768, ushort.MaxValue, '\uffff', -1, int.MinValue,
                int.MaxValue, uint.MaxValue, long.MinValue, long.MaxValue, ulong.MaxValue,
                2.9f, float.NaN, float.PositiveInfinity, -2.9, double.NaN, double.NegativeInfinity,
                double.MaxValue, 18446744073709549568d, 9223372036854774784d, 42m, Example.Value
            };
            var targets = new[] { typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(char),
                typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(Example) };
            foreach (var value in values)
            foreach (var target in targets)
            foreach (var check in new[] { false, true })
            {
                if ((value is decimal && target.IsEnum) || (value.GetType().IsEnum && target == typeof(decimal))) continue;
                var input = Expression.Constant(value);
                var converted = check ? Expression.ConvertChecked(input, target) : Expression.Convert(input, target);
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(converted, typeof(object))));
            }
        }

        [Fact]
        public void Forced_nullable_and_unboxing_conversions_match_CLR()
        {
            foreach (var value in new int?[] { null, -1, 42 })
            foreach (var target in new[] { typeof(int), typeof(long), typeof(long?), typeof(object) })
            {
                var input = Expression.Constant(value, typeof(int?));
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(Expression.Convert(input, target), typeof(object))));
            }
            foreach (var value in new object[] { null, 1, 1L })
            foreach (var target in new[] { typeof(int), typeof(long), typeof(int?) })
            {
                var input = Expression.Constant(value, typeof(object));
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(Expression.Convert(input, target), typeof(object))));
            }
        }

        [Fact]
        public void Forced_nullable_receivers_and_coalesce_match_CLR()
        {
            foreach (var value in new int?[] { null, 42 })
            {
                var array = new[] { value };
                Compare(() => array[0].HasValue);
                Compare(() => array[0].Value);
                Compare(() => array[0].GetValueOrDefault());
                Compare(() => array[0].GetValueOrDefault(9));
                Compare(() => array[0].Equals(42));
                Compare(() => array[0].GetHashCode());
                Compare(() => array[0].ToString());
                Compare(() => array[0] ?? 2L);
            }
        }

        [Fact]
        public void Coalesce_converts_either_operand_to_its_result_type()
        {
            foreach (var value in new long?[] { null, 42L })
            {
                var coalesce = Expression.Coalesce(Expression.Constant(value, typeof(long?)), Expression.Constant(9));
                Assert.Equal(typeof(long), coalesce.Type);
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(coalesce, typeof(object))));
            }
        }

        public struct Number
        {
            public int Value;
            public static explicit operator int(Number value) => value.Value;
        }

        [Fact]
        public void Lifted_conversion_operators_preserve_null_and_exception_semantics()
        {
            foreach (var value in new decimal?[] { null, 2.9m })
            foreach (var target in new[] { typeof(int), typeof(int?) })
            {
                var input = Expression.Constant(value, typeof(decimal?));
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(Expression.Convert(input, target), typeof(object))));
            }
            foreach (var value in new Number?[] { null, new Number { Value = 42 } })
            foreach (var target in new[] { typeof(int), typeof(int?) })
            {
                var input = Expression.Constant(value, typeof(Number?));
                Compare(Expression.Lambda<Func<object>>(Expression.Convert(Expression.Convert(input, target), typeof(object))));
            }
        }

        public class Receiver { public int Call(int value) => value; }
        private static int ThrowArgument() => throw new ArgumentException("argument evaluated first");

        [Fact]
        public void Null_receiver_evaluates_arguments_before_failing()
        {
            Receiver receiver = null;
            Compare(() => receiver.Call(ThrowArgument()));
            Func<int, int> function = null;
            Compare(() => function(ThrowArgument()));
            function = value => value;
            Compare(() => function(42));
        }

        private static void Compare(Expression<Func<object>> expression)
        {
            object expected = null, actual = null;
            var expectedError = Record.Exception(() => expected = expression.Compile()());
            RuntimeExpression.ForceInterpretation = true;
            Exception actualError;
            try { actualError = Record.Exception(() => actual = RuntimeExpression.Compile(expression)()); }
            finally { RuntimeExpression.ForceInterpretation = false; }
            Assert.True(expectedError?.GetType() == actualError?.GetType(),
                expression + ": expected " + expectedError?.GetType() + ", actual " + actualError?.GetType());
            Assert.True(Equals(expected, actual), expression + ": expected " + expected + ", actual " + actual);
        }
    }
}
