using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB.Tests.Mapper
{
    internal sealed class LinqCacheWideFuzzGenerator
    {
        internal const int Kinds = 8;

        private static readonly string[] Words = { "a", "Ready", "READY", "ready", "", "zz" };
        private static readonly MethodInfo AnyMethod = EnumerableMethod(nameof(Enumerable.Any), 2);
        private static readonly MethodInfo AllMethod = EnumerableMethod(nameof(Enumerable.All), 2);
        private static readonly MethodInfo WhereMethod = EnumerableMethod(nameof(Enumerable.Where), 2);
        private static readonly MethodInfo SelectMethod = EnumerableMethod(nameof(Enumerable.Select), 2);
        private static readonly MethodInfo ToArrayMethod = EnumerableMethod(nameof(Enumerable.ToArray), 1);

        private readonly ParameterExpression _row;
        private readonly Random _shape;
        private readonly Random _values;

        internal LinqCacheWideFuzzGenerator(int shapeSeed, int valueSeed)
        {
            _row = Expression.Parameter(shapeSeed % 7 == 0 ? typeof(IWideFuzzRow) : typeof(WideFuzzRow), "x");
            _shape = new Random(shapeSeed);
            _values = new Random(unchecked(valueSeed * 7919 + 17));
        }

        internal LambdaExpression Build(int kind)
        {
            Expression body;
            Type result;
            switch (kind)
            {
                case 0: body = Bool(4); result = typeof(bool); break;
                case 1: body = Int(4); result = typeof(int); break;
                case 2: body = Array(4); result = typeof(object[]); break;
                case 3: body = Date(4); result = typeof(DateTime); break;
                case 4: body = IntArray(4); result = typeof(int[]); break;
                case 5: body = Init(4); result = typeof(WideFuzzProjection); break;
                case 6: body = Bool(4); result = typeof(bool); break;
                default: body = Int(4); result = typeof(int); break;
            }
            return Expression.Lambda(typeof(Func<,>).MakeGenericType(_row.Type, result), body, _row);
        }

        private Expression Int(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 6 : 12))
            {
                case 0: return Property(_row, nameof(WideFuzzRow.Value));
                case 1: return Property(Property(_row, nameof(WideFuzzRow.Next)), nameof(WideFuzzRow.Value));
                case 2: return Capture(_values.Next(-20, 80));
                case 3: return Expression.Constant(_shape.Next(-5, 10));
                case 4: return Expression.Coalesce(Property(_row, nameof(WideFuzzRow.Optional)), Capture(_values.Next(10)));
                case 5: return Property(Property(_row, nameof(WideFuzzRow.Optional)), nameof(Nullable<int>.Value));
                case 6: return Expression.Add(Int(depth - 1), Int(depth - 1));
                case 7: return Expression.Multiply(Int(depth - 1), Int(depth - 1));
                case 8: return Expression.Condition(Bool(depth - 1), Int(depth - 1), Int(depth - 1));
                case 9: return Expression.Call(MathMethod(_shape.Next(2) == 0 ? "Min" : "Max"), Int(depth - 1), Int(depth - 1));
                case 10: return Property(Date(depth - 1), nameof(DateTime.Year));
                default: return Expression.ArrayLength(Property(_row, nameof(WideFuzzRow.Items)));
            }
        }

        private Expression Date(int depth)
        {
            var captured = Capture(ValueDate());
            switch (_shape.Next(depth <= 0 ? 3 : 6))
            {
                case 0: return Property(_row, nameof(WideFuzzRow.When));
                case 1: return captured;
                case 2: return Property(captured, nameof(DateTime.Date));
                case 3: return Expression.Call(Date(depth - 1), DateMethod(nameof(DateTime.AddDays)), Capture((double)_values.Next(-4, 5)));
                case 4: return Property(Date(depth - 1), nameof(DateTime.Date));
                default: return Expression.Condition(Bool(depth - 1), Date(0), Date(0));
            }
        }

        private Expression Bool(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 8 : 15))
            {
                case 0: return Compare(Int(depth - 1), Int(depth - 1));
                case 1: return Expression.Equal(Text(depth - 1), Text(depth - 1));
                case 2: return Compare(Date(depth - 1), Date(depth - 1));
                case 3: return Expression.Equal(Property(_row, nameof(WideFuzzRow.Optional)), Capture((int?)ValueInt()));
                case 4: return Expression.Property(Property(_row, nameof(WideFuzzRow.Optional)), nameof(Nullable<int>.HasValue));
                case 5: return Quantifier(AnyMethod, depth);
                case 6: return Quantifier(AllMethod, depth);
                case 7: return Expression.Call(Property(_row, nameof(WideFuzzRow.Name)), StringMethod(), Text(0));
                case 8: return Expression.AndAlso(Bool(depth - 1), Bool(depth - 1));
                case 9: return Expression.OrElse(Bool(depth - 1), Bool(depth - 1));
                case 10: return Expression.Not(Bool(depth - 1));
                case 11: return Compare(Expression.ArrayLength(Property(_row, nameof(WideFuzzRow.Items))), Capture(ValueInt()));
                case 12: return Expression.GreaterThan(Property(Date(depth - 1), nameof(DateTime.Year)), Capture(2018 + _values.Next(8)));
                case 13: return Expression.Equal(Property(Date(depth - 1), nameof(DateTime.Date)), Property(Capture(ValueDate()), nameof(DateTime.Date)));
                default: return Compare(Expression.ArrayLength(Property(_row, nameof(WideFuzzRow.Items))), Capture(ValueInt()));
            }
        }

        private Expression Text(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 3 : 5))
            {
                case 0: return Property(_row, nameof(WideFuzzRow.Name));
                case 1: return Capture(Words[_values.Next(Words.Length)]);
                case 2: return Expression.Constant(Words[_shape.Next(Words.Length)]);
                case 3: return Expression.Call(Text(depth - 1), typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes));
                default: return Expression.Call(Text(depth - 1), typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes));
            }
        }

        private Expression Quantifier(MethodInfo method, int depth)
        {
            var item = Expression.Parameter(typeof(int), "i");
            var predicate = Expression.Lambda<Func<int, bool>>(
                Compare(item, depth > 0 ? Int(depth - 1) : Capture(ValueInt())), item);
            return Expression.Call(method.MakeGenericMethod(typeof(int)), Property(_row, nameof(WideFuzzRow.Items)), predicate);
        }

        private Expression IntArray(int depth)
        {
            var item = Expression.Parameter(typeof(int), "i");
            Expression source = Property(_row, nameof(WideFuzzRow.Items));
            if (_shape.Next(2) == 0)
            {
                var filter = Expression.Lambda<Func<int, bool>>(Compare(item, Capture(ValueInt())), item);
                source = Expression.Call(WhereMethod.MakeGenericMethod(typeof(int)), source, filter);
            }
            var selector = Expression.Lambda<Func<int, int>>(Expression.Add(item, Capture(_values.Next(-3, 4))), item);
            source = Expression.Call(SelectMethod.MakeGenericMethod(typeof(int), typeof(int)), source, selector);
            return Expression.Call(ToArrayMethod.MakeGenericMethod(typeof(int)), source);
        }

        private Expression Array(int depth)
        {
            var items = new Expression[_shape.Next(4)];
            for (var i = 0; i < items.Length; i++)
            {
                Expression value = depth > 0 && _shape.Next(4) == 0 ? Array(depth - 1) :
                    _shape.Next(2) == 0 ? Int(Math.Max(0, depth - 1)) : Text(Math.Max(0, depth - 1));
                items[i] = Expression.Convert(value, typeof(object));
            }
            return Expression.NewArrayInit(typeof(object), items);
        }

        private Expression Init(int depth)
        {
            var captured = Capture(_values.Next(4) == 0 ? null : new WideFuzzRow { Id = _values.Next(1, 20) });
            var bindings = new List<MemberBinding>();
            if (_shape.Next(2) == 0) bindings.Add(Expression.Bind(typeof(WideFuzzProjection).GetProperty(nameof(WideFuzzProjection.Value)), Int(Math.Max(0, depth - 1))));
            if (depth > 0 && _shape.Next(3) == 0) bindings.Add(Expression.Bind(typeof(WideFuzzProjection).GetProperty(nameof(WideFuzzProjection.Next)), Init(depth - 1)));
            bindings.Add(Expression.Bind(typeof(WideFuzzProjection).GetProperty(nameof(WideFuzzProjection.Item)), captured));
            bindings.Add(Expression.Bind(typeof(WideFuzzProjection).GetProperty(nameof(WideFuzzProjection.Items)), Expression.NewArrayInit(typeof(WideFuzzRow), captured)));
            return Expression.MemberInit(Expression.New(typeof(WideFuzzProjection)), bindings);
        }

        private Expression Compare(Expression left, Expression right)
        {
            switch (_shape.Next(6))
            {
                case 0: return Expression.Equal(left, right);
                case 1: return Expression.NotEqual(left, right);
                case 2: return Expression.LessThan(left, right);
                case 3: return Expression.LessThanOrEqual(left, right);
                case 4: return Expression.GreaterThan(left, right);
                default: return Expression.GreaterThanOrEqual(left, right);
            }
        }

        private DateTime ValueDate() => new DateTime(2019 + _values.Next(7), 1 + _values.Next(12), 1 + _values.Next(27),
            _values.Next(24), _values.Next(60), 0, _values.Next(2) == 0 ? DateTimeKind.Utc : DateTimeKind.Local);

        private int ValueInt() => _values.Next(-5, 20);
        private static Expression Property(Expression owner, string name) => Expression.Property(owner, name);
        private MethodInfo StringMethod() => typeof(string).GetMethod(new[] { nameof(string.StartsWith), nameof(string.EndsWith), nameof(string.Contains) }[_shape.Next(3)], new[] { typeof(string) });
        private static MethodInfo MathMethod(string name) => typeof(Math).GetMethod(name, new[] { typeof(int), typeof(int) });
        private static MethodInfo DateMethod(string name) => typeof(DateTime).GetMethod(name, new[] { typeof(double) });
        private static MethodInfo EnumerableMethod(string name, int count) => typeof(Enumerable).GetMethods().Single(x => x.Name == name && x.GetParameters().Length == count && x.GetParameters().Last().ParameterType.IsGenericType && x.GetParameters().Last().ParameterType.GetGenericTypeDefinition() == (count == 1 ? typeof(IEnumerable<>) : typeof(Func<,>)));

        private static Expression Capture<T>(T value) =>
            Expression.Field(Expression.Constant(new Closure<T> { Item = value }), nameof(Closure<T>.Item));

        private sealed class Closure<T> { public T Item; }
    }
}
