using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB.Tests.Mapper
{
    /// <summary>
    /// Builds random LINQ lambdas for the cache differential test. The shape seed alone
    /// decides the tree structure; the value seed only changes captured values, so the
    /// same shape seed with another value seed must be a cache hit that rebinds.
    /// </summary>
    internal sealed class LinqCacheFuzzGenerator
    {
        internal const int Kinds = 4;

        private static readonly string[] Words = { "a", "Ready", "READY", "ready", "", "zz" };
        private static readonly StringComparison[] Modes =
        {
            StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase,
            StringComparison.CurrentCulture, StringComparison.InvariantCultureIgnoreCase
        };
        private static readonly MethodInfo CountMethod = typeof(LinqCacheFuzzGenerator).GetMethod(nameof(Count));
        private static readonly MethodInfo ListContains = typeof(List<int>).GetMethod(nameof(List<int>.Contains));

        private readonly ParameterExpression _row = Expression.Parameter(typeof(FuzzRow), "x");
        private readonly Random _shape;
        private readonly Random _values;

        internal LinqCacheFuzzGenerator(int shapeSeed, int valueSeed)
        {
            _shape = new Random(shapeSeed);
            _values = new Random(valueSeed);
        }

        public static int Count(object[] values) => values.Length;

        internal LambdaExpression Build(int kind)
        {
            switch (kind)
            {
                case 0: return Expression.Lambda<Func<FuzzRow, bool>>(Bool(3), _row);
                case 1: return Expression.Lambda<Func<FuzzRow, int>>(Int(3), _row);
                case 2: return Expression.Lambda<Func<FuzzRow, object[]>>(Array(3), _row);
                default: return Expression.Lambda<Func<FuzzRow, FuzzRow>>(Init(3), _row);
            }
        }

        private Expression Int(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 4 : 9))
            {
                case 0: return Expression.Property(_row, nameof(FuzzRow.Value));
                case 1: return Expression.Property(Expression.Property(_row, nameof(FuzzRow.Next)), nameof(FuzzRow.Value));
                case 2: return Capture(_values.Next(-5, 50));
                case 3: return Expression.Constant(_shape.Next(4));
                case 4: return Expression.Add(Int(depth - 1), Int(depth - 1));
                case 5: return Expression.Multiply(Int(depth - 1), Int(depth - 1));
                case 6: return Expression.Condition(Bool(depth - 1), Int(depth - 1), Int(depth - 1));
                case 7: return Expression.Call(_shape.Next(2) == 0 ? MathMethod("Min") : MathMethod("Max"), Int(depth - 1), Int(depth - 1));
                default: return Expression.Call(CountMethod, ClosedArray(depth - 1));
            }
        }

        private Expression Text(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 3 : 5))
            {
                case 0: return Expression.Property(_row, nameof(FuzzRow.Name));
                case 1: return Capture(Words[_values.Next(Words.Length)]);
                case 2: return Expression.Constant(Words[_shape.Next(Words.Length)]);
                case 3: return Expression.Call(Text(depth - 1), typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes));
                default: return Expression.Call(Text(depth - 1), typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes));
            }
        }

        private Expression Bool(int depth)
        {
            switch (_shape.Next(depth <= 0 ? 7 : 11))
            {
                case 0: return Compare(Int(depth - 1), Int(depth - 1));
                case 1: return Expression.Equal(Text(depth - 1), Text(depth - 1));
                case 2: return Expression.Call(Expression.Property(_row, nameof(FuzzRow.Name)), StringMethod(), Text(0));
                case 3: return StringEquals();
                case 4: return EnumEquals();
                case 5: return Expression.Call(Capture(Enumerable.Range(0, _values.Next(4)).ToList()), ListContains, Int(0));
                case 6: return Expression.Equal(Expression.Property(Expression.Property(_row, nameof(FuzzRow.Tags)), "Item",
                    Capture(Words[_values.Next(3)] + "k")), Int(0));
                case 7: return Expression.AndAlso(Bool(depth - 1), Bool(depth - 1));
                case 8: return Expression.OrElse(Bool(depth - 1), Bool(depth - 1));
                case 9: return Expression.Not(Bool(depth - 1));
                default: return Expression.Equal(Array(depth - 1), Array(depth - 1));
            }
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

        private Expression StringEquals()
        {
            // The comparison mode selects the translation, so it must never be reused
            // from a cached shape: vary it by value seed, both captured and inline.
            var mode = Modes[_values.Next(Modes.Length)];
            var argument = _shape.Next(2) == 0 ? Capture(mode) : Expression.Constant(mode);
            var name = Expression.Property(_row, nameof(FuzzRow.Name));
            if (_shape.Next(2) == 0)
                return Expression.Call(name, typeof(string).GetMethod(nameof(string.Equals),
                    new[] { typeof(string), typeof(StringComparison) }), Text(0), argument);
            return Expression.Call(typeof(string).GetMethod(nameof(string.Equals),
                new[] { typeof(string), typeof(string), typeof(StringComparison) }), name, Text(0), argument);
        }

        private Expression EnumEquals()
        {
            var state = Expression.Property(_row, nameof(FuzzRow.State));
            var value = (FuzzState)_values.Next(3);
            switch (_shape.Next(3))
            {
                // The C# compiler emits enum comparisons through integer conversions.
                case 0: return Expression.Equal(Expression.Convert(state, typeof(int)),
                    Expression.Convert(Capture(value), typeof(int)));
                case 1: return Expression.Equal(Capture(value), state);
                default:
                    // The runtime type behind an object capture changes between calls.
                    var boxed = new object[] { value, null, "Ready", 1, FuzzOther.Ready }[_values.Next(5)];
                    return Expression.Call(state, typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object) }),
                        Capture(boxed));
            }
        }

        private Expression Array(int depth)
        {
            var items = new Expression[_shape.Next(3)];
            for (var i = 0; i < items.Length; i++) items[i] = Expression.Convert(Element(depth - 1), typeof(object));
            return Expression.NewArrayInit(typeof(object), items);
        }

        private Expression ClosedArray(int depth)
        {
            var items = new Expression[_shape.Next(3)];
            for (var i = 0; i < items.Length; i++)
                items[i] = depth > 0 && _shape.Next(2) == 0 ? ClosedArray(depth - 1) :
                    Expression.Convert(Capture(_values.Next(9)), typeof(object));
            return Expression.NewArrayInit(typeof(object), items);
        }

        private Expression Element(int depth)
        {
            if (depth <= 0) return _shape.Next(2) == 0 ? Int(0) : Text(0);
            switch (_shape.Next(4))
            {
                case 0: return Int(depth);
                case 1: return Text(depth);
                case 2: return Array(depth);
                default: return Init(depth);
            }
        }

        private Expression Init(int depth)
        {
            var bindings = new List<MemberBinding>();
            if (_shape.Next(2) == 0) bindings.Add(Expression.Bind(typeof(FuzzRow).GetProperty(nameof(FuzzRow.Value)), Int(depth - 1)));
            if (_shape.Next(2) == 0) bindings.Add(Expression.Bind(typeof(FuzzRow).GetProperty(nameof(FuzzRow.Name)), Text(depth - 1)));
            if (depth > 0 && _shape.Next(2) == 0)
                bindings.Add(Expression.Bind(typeof(FuzzRow).GetProperty(nameof(FuzzRow.Next)), Init(depth - 1)));
            return Expression.MemberInit(Expression.New(typeof(FuzzRow)), bindings);
        }

        private MethodInfo StringMethod()
        {
            var name = new[] { nameof(string.StartsWith), nameof(string.EndsWith), nameof(string.Contains) }[_shape.Next(3)];
            return typeof(string).GetMethod(name, new[] { typeof(string) });
        }

        private static MethodInfo MathMethod(string name) => typeof(Math).GetMethod(name, new[] { typeof(int), typeof(int) });

        // The C# compiler turns a captured local into a field read on a closure
        // constant; build the same node pair so the shape visitor sees real captures.
        private static Expression Capture<T>(T value) =>
            Expression.Field(Expression.Constant(new Closure<T> { Item = value }), nameof(Closure<T>.Item));

        private sealed class Closure<T>
        {
            public T Item;
        }
    }

    public enum FuzzState { New, Ready, Done }
    public enum FuzzOther { New, Ready }

    public class FuzzRow
    {
        public int Value { get; set; }
        public string Name { get; set; }
        public FuzzState State { get; set; }
        public FuzzRow Next { get; set; }
        public Dictionary<string, int> Tags { get; set; }
    }
}
