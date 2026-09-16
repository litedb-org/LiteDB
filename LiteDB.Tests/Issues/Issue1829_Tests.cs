using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Tests.Database;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1829_Tests
    {
        public class Product
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<int> Scores { get; set; }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Predicate_builder_or_matches_direct_linq_in_both_operand_orders(bool falseFirst)
        {
            var products = CreateProducts();
            Expression<Func<Product, bool>> alwaysFalse = p => false;
            Expression<Func<Product, bool>> containsLowercaseA = p => p.Name.Contains("a");
            Expression<Func<Product, bool>> direct = p => false || p.Name.Contains("a");
            var composed = falseFirst
                ? alwaysFalse.Or(containsLowercaseA)
                : containsLowercaseA.Or(alwaysFalse);

            ReferenceEquals(alwaysFalse.Parameters[0], containsLowercaseA.Parameters[0]).Should().BeFalse();
            AssertTruthTable(products, containsLowercaseA, true, false, true, false);
            AssertTruthTable(products, direct, true, false, true, false);
            AssertTruthTable(products, composed, true, false, true, false);

            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Product>("products");
            collection.InsertBulk(products).Should().Be(4);
            collection.EnsureIndex(p => p.Name).Should().BeTrue();

            AssertQuery(collection, containsLowercaseA, 4, 7);
            AssertQuery(collection, direct, 4, 7);
            AssertQuery(collection, composed, 4, 7);
            collection.Count().Should().Be(4);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Predicate_builder_or_keeps_matches_from_each_independent_predicate(bool idPredicateFirst)
        {
            var products = CreateProducts();
            Expression<Func<Product, bool>> idIsFive = p => p.Id == 5;
            Expression<Func<Product, bool>> containsLowercaseA = p => p.Name.Contains("a");
            Expression<Func<Product, bool>> direct = p => p.Id == 5 || p.Name.Contains("a");
            var composed = idPredicateFirst
                ? idIsFive.Or(containsLowercaseA)
                : containsLowercaseA.Or(idIsFive);

            AssertTruthTable(products, idIsFive, false, true, false, false);
            AssertTruthTable(products, containsLowercaseA, true, false, true, false);
            AssertTruthTable(products, direct, true, true, true, false);
            AssertTruthTable(products, composed, true, true, true, false);

            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Product>("products");
            collection.InsertBulk(products).Should().Be(4);
            collection.EnsureIndex(p => p.Name).Should().BeTrue();

            AssertQuery(collection, direct, 4, 5, 7);
            AssertQuery(collection, composed, 4, 5, 7);
            collection.Count().Should().Be(4);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Predicate_builder_or_preserves_same_named_nested_parameter_scopes(bool falseFirst)
        {
            var products = CreateProducts();
            Expression<Func<Product, bool>> alwaysFalse = p => false;
            Expression<Func<Product, bool>> direct = product =>
                product.Scores.Any(score => score > product.Id);
            var nested = CreateSameNamedNestedPredicate(out var innerParameter);
            var composed = falseFirst
                ? alwaysFalse.Or(nested)
                : nested.Or(alwaysFalse);

            nested.Parameters[0].Name.Should().Be(innerParameter.Name);
            ReferenceEquals(nested.Parameters[0], innerParameter).Should().BeFalse();
            AssertTruthTable(products, direct, true, false, true, false);
            AssertTruthTable(products, nested, true, false, true, false);
            AssertTruthTable(products, composed, true, false, true, false);

            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<Product>("products");
            collection.InsertBulk(products).Should().Be(4);

            AssertQuery(collection, direct, 4, 7);
            AssertQuery(collection, nested, 4, 7);
            AssertQuery(collection, composed, 4, 7);
            collection.Count().Should().Be(4);
        }

        private static Product[] CreateProducts()
        {
            return new[]
            {
                new Product { Id = 4, Name = "Vantage", Scores = new List<int> { 3, 5 } },
                new Product { Id = 5, Name = "Corvette", Scores = new List<int> { 1, 5 } },
                new Product { Id = 7, Name = "Caprice", Scores = new List<int> { 8 } },
                new Product { Id = 6, Name = "3000GT", Scores = new List<int>() }
            };
        }

        private static Expression<Func<Product, bool>> CreateSameNamedNestedPredicate(
            out ParameterExpression innerParameter)
        {
            var outerParameter = Expression.Parameter(typeof(Product), "p");
            innerParameter = Expression.Parameter(typeof(int), "p");
            var scores = Expression.Property(outerParameter, nameof(Product.Scores));
            var outerId = Expression.Property(outerParameter, nameof(Product.Id));
            var innerPredicate = Expression.Lambda<Func<int, bool>>(
                Expression.GreaterThan(innerParameter, outerId),
                innerParameter);
            var any = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Any),
                new[] { typeof(int) },
                scores,
                innerPredicate);

            return Expression.Lambda<Func<Product, bool>>(any, outerParameter);
        }

        private static int[] QueryIds(
            ILiteCollection<Product> collection,
            Expression<Func<Product, bool>> predicate)
        {
            return collection.Query()
                .Where(predicate)
                .ToList()
                .Select(p => p.Id)
                .OrderBy(id => id)
                .ToArray();
        }

        private static void AssertQuery(
            ILiteCollection<Product> collection,
            Expression<Func<Product, bool>> predicate,
            params int[] expectedIds)
        {
            var ids = QueryIds(collection, predicate);
            var count = collection.Query().Where(predicate).Count();

            using var scope = new AssertionScope();
            AssertIds(ids, expectedIds);
            count.Should().Be(expectedIds.Length);
            count.Should().Be(ids.Length, "Count() and enumeration must describe the same result set");
        }

        private static void AssertTruthTable(
            IEnumerable<Product> products,
            Expression<Func<Product, bool>> predicate,
            params bool[] expected)
        {
            products.Select(predicate.Compile()).Should().Equal(expected);
        }

        private static void AssertIds(int[] actual, params int[] expected)
        {
            actual.Should().HaveCount(expected.Length);
            actual.Should().Equal(expected);
        }
    }
}
