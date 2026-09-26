using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class StructuredExpression_Tests
    {
        [Fact]
        public void Direct_and_parsed_expressions_execute_identically_without_shared_compilation_cache()
        {
            var minimum = 21;
            var prefix = "Al";
            var ids = new[] { 1, 3 };
            Expression<Func<Person, object>> mapped = p => p.Ages.Select(a => a + minimum).ToArray();
            Expression<Func<Person, object>> filtered = p => p.Ages.Where(a => a >= minimum).ToArray();
            Expression<Func<Person, object>> projected = p => new { p.Id, p.Name, Ages = p.Ages.Where(a => a >= minimum).ToArray() };
            var expressions = new Expression<Func<Person, object>>[]
            {
                p => p.Age >= minimum && p.Name.StartsWith(prefix),
                p => ids.Contains(p.Id),
                p => p.Age > minimum ? p.Name : "young",
                p => p.Name ?? "missing",
                p => p.Age + p.Id * 2,
                p => !p.Active,
                p => p.Active && p.Age > minimum,
                mapped,
                filtered,
                p => p.Ages.Where(a => a > p.Age).Count(),
                p => p.Ages.Any(a => a >= minimum),
                p => p.Ages.All(a => a < minimum),
                p => p.Ages.ElementAt(1),
                p => p.Ages.ElementAt(minimum + 1),
                projected,
                p => new Person { Id = p.Id, Name = p.Name, Ages = new[] { p.Age, minimum } },
                p => p.CreatedOn.Year,
                p => new DateTime(p.CreatedOn.Year, 1, 1),
                p => (int)p.Salary,
                p => p.Optional.HasValue,
                p => p.Optional ?? minimum,
                p => p.Name.Trim().ToUpper(),
                p => p.Names.Any(n => n.StartsWith(prefix)),
                p => p.Name.Contains(prefix) || p.Name.EndsWith("z"),
                p => Math.Round(p.Salary, 1)
            };
            var mapper = new BsonMapper();
            var random = new Random(1847);
            foreach (var expression in expressions)
            {
                BsonExpression direct;
                using (new DirectTranslationScope()) direct = mapper.GetExpression(expression);
                BsonExpression parsed;
                BsonExpression.DisableCompilationCache = true;
                try { parsed = BsonExpression.Create(direct.Source, direct.Parameters); }
                finally { BsonExpression.DisableCompilationCache = false; }
                if (expression == mapped || expression == filtered || expression == projected)
                    parsed = ExpressionParity.WithSelectorDependency(parsed);
                ExpressionParity.AssertMetadata(direct, parsed);
                for (var i = 0; i < 40; i++)
                {
                    var document = mapper.ToDocument(new Person
                    {
                        Id = i, Age = random.Next(1, 80), Name = i % 2 == 0 ? "Alice" : "Bob",
                        Active = i % 2 == 0, Ages = new[] { 10, random.Next(1, 80), 40 },
                        Names = new[] { "Alex", "Bill" }, Optional = i % 3 == 0 ? (int?)null : i,
                        Salary = random.NextDouble() * 100, CreatedOn = new DateTime(2020, 3, 4)
                    });
                    direct.Execute(document).Should().Equal(parsed.Execute(document), expression.ToString());
                }
            }
        }

        [Fact]
        public void Random_boolean_trees_preserve_structure_and_execution()
        {
            var random = new Random(1955);
            var parameter = Expression.Parameter(typeof(Person), "p");
            var age = Expression.Property(parameter, nameof(Person.Age));
            var mapper = new BsonMapper();
            for (var i = 0; i < 100; i++)
            {
                Expression body = Expression.GreaterThan(age, Expression.Constant(random.Next(80)));
                for (var j = 0; j < 4; j++)
                {
                    var term = Expression.LessThan(age, Expression.Constant(random.Next(80)));
                    body = random.Next(2) == 0 ? Expression.AndAlso(body, term) : Expression.OrElse(body, term);
                }
                var lambda = Expression.Lambda<Func<Person, bool>>(body, parameter);
                BsonExpression direct;
                using (new DirectTranslationScope()) direct = mapper.GetExpression(lambda);
                var parsed = BsonExpression.Create(direct.Source, direct.Parameters);
                ExpressionParity.AssertMetadata(direct, parsed);
                var compiled = lambda.Compile();
                foreach (var value in Enumerable.Range(0, 80))
                {
                    var person = new Person { Age = value };
                    var document = mapper.ToDocument(person);
                    direct.ExecuteScalar(document).Should().Be(parsed.ExecuteScalar(document));
                    direct.ExecuteScalar(document).AsBoolean.Should().Be(compiled(person));
                }
            }
        }

        [Fact]
        public async Task Bound_nested_expressions_are_independent_and_concurrent()
        {
            var minimum = 0;
            var mapper = new BsonMapper();
            var template = mapper.GetExpression<Person, object>(p => p.Ages.Where(a => a >= minimum).Select(a => a + minimum).ToArray());
            var document = mapper.ToDocument(new Person { Ages = new[] { 1, 5, 10 } });
            var tasks = Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            {
                var binding = new BsonDocument { ["p0"] = i, ["p1"] = i };
                BsonExpression bound;
                using (new DirectTranslationScope()) bound = template.Bind(binding);
                for (var j = 0; j < 30; j++)
                {
                    bound.ExecuteScalar(document).AsArray.Select(x => x.AsInt32)
                        .Should().Equal(new[] { 1, 5, 10 }.Where(x => x >= i).Select(x => x + i));
                }
            }));
            await Task.WhenAll(tasks);
            template.Parameters["p0"].AsInt32.Should().Be(0);
        }

        [Fact]
        public void Repeated_translation_and_nested_parameter_indices_use_current_values()
        {
            var mapper = new BsonMapper();
            var document = mapper.ToDocument(new Person { Ages = new[] { 3, 8, 12 } });
            for (var minimum = 0; minimum < 15; minimum++)
            {
                var expression = mapper.GetExpression<Person, int>(p => p.Ages.Where(a => a > minimum).Count());
                expression.ExecuteScalar(document).AsInt32.Should().Be(new[] { 3, 8, 12 }.Count(x => x > minimum));
                var index = minimum % 3;
                var element = mapper.GetExpression<Person, int>(p => p.Ages.ElementAt(index));
                element.ExecuteScalar(document).AsInt32.Should().Be(new[] { 3, 8, 12 }[index]);
            }
        }

        [Fact]
        public void Mappers_and_collations_do_not_share_translation_assumptions()
        {
            var first = new BsonMapper { ResolveFieldName = name => "first_" + name };
            var second = new BsonMapper { ResolveFieldName = name => "second_" + name };
            var a = first.GetExpression<Person, bool>(p => p.Name == "ALICE");
            var b = second.GetExpression<Person, bool>(p => p.Name == "ALICE");
            a.Source.Should().Contain("first_Name");
            b.Source.Should().Contain("second_Name");
            var document = first.ToDocument(new Person { Name = "alice" });
            a.ExecuteScalar(document, Collation.Binary).AsBoolean.Should().BeFalse();
            a.ExecuteScalar(document, new Collation("en-US/IgnoreCase")).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Mapped_field_names_are_structural_and_escaped_only_for_diagnostics()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Person>().Field(p => p.Name, "weird'field.with.dot");
            BsonExpression expression;
            using (new DirectTranslationScope()) expression = mapper.GetExpression<Person, string>(p => p.Name);
            expression.ExecuteScalar(mapper.ToDocument(new Person { Name = "Alice" })).AsString.Should().Be("Alice");
            var parsed = BsonExpression.Create(expression.Source);
            ExpressionParity.AssertMetadata(expression, parsed);
        }

        [Fact]
        public void Unsupported_nodes_identify_the_operator_and_original_expression()
        {
            Expression<Func<Person, bool>> expression = p => p.Age % 2 == 0;
            var exception = Assert.Throws<NotSupportedException>(() => new BsonMapper().GetExpression(expression));
            exception.Message.Should().Contain("Modulo").And.Contain(expression.ToString());
        }

        public class Person
        {
            public int Id { get; set; }
            public int Age { get; set; }
            public string Name { get; set; }
            public bool Active { get; set; }
            public int[] Ages { get; set; }
            public string[] Names { get; set; }
            public int? Optional { get; set; }
            public double Salary { get; set; }
            public DateTime CreatedOn { get; set; }
        }
    }
}
