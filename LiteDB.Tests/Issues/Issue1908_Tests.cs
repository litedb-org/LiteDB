using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1908_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string[] Factors { get; set; }
            public Dictionary<string, string> Values { get; set; }
        }

        public class WrappedInt
        {
            public int Value { get; set; }
            public static implicit operator int(WrappedInt wrapped) => wrapped.Value;
        }

        [Fact]
        public void Captured_element_conversions_keep_CLR_values()
        {
            var wrappers = new[] { new WrappedInt { Value = 2 } };
            var numbers = new[] { 2.9 };
            var negatives = new[] { -2 };
            var masks = new[] { ~2 };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1 }, new Row { Id = 2 }, new Row { Id = 3 } });
            Expression<Func<Row, bool>> predicate = x => x.Id == (int)wrappers[0];
            col.Find(predicate).Select(x => x.Id).Should().Equal(2);
            wrappers[0].Value = 1;
            col.Find(predicate).Select(x => x.Id).Should().Equal(1);
            col.Find(x => x.Id == (int)numbers[0]).Select(x => x.Id).Should().Equal(2);
            col.Find(x => x.Id == -negatives[0]).Select(x => x.Id).Should().Equal(2);
            col.Find(x => x.Id == ~masks[0]).Select(x => x.Id).Should().Equal(2);
        }

        [Fact]
        public void Captured_array_lengths_keep_CLR_null_behavior_and_runtime_arrays_keep_LENGTH()
        {
            var mapper = new BsonMapper();
            var values = new[] { new[] { 1, 2 } };
            Expression<Func<Row, int>> expression = x => values[0].Length;
            mapper.GetExpression<Row, int>(expression).ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(2);
            values[0] = null;
            Action missing = () => mapper.GetExpression<Row, int>(expression);
            missing.Should().Throw<Exception>().Which.GetBaseException().Should().BeOfType<NullReferenceException>();

            var offsets = new[] { 1 };
            var runtime = mapper.GetExpression<Row, int>(x => offsets.Select(o => new[] { DateTime.Now.Year + o })
                .First(a => a.Length > 0).Length);
            runtime.Source.Should().Contain("NOW()");
            runtime.ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(1);
        }

        [Fact]
        public void Runtime_unary_plus_and_safe_widening_keep_server_evaluation()
        {
            var mapper = new BsonMapper();
            var offsets = new[] { 1 };
            var longOffsets = new[] { 1L };
            Expression<Func<Row, int>> value = x => DateTime.Now.Year + offsets[0];
            var positive = Expression.Lambda<Func<Row, int>>(Expression.UnaryPlus(value.Body), value.Parameters);
            mapper.GetExpression<Row, int>(positive).Source.Should().Contain("NOW()");
            mapper.GetExpression<Row, long>(x => checked((long)offsets.First(o => o < DateTime.Now.Year)))
                .Source.Should().Contain("NOW()");
            mapper.GetExpression<Row, double>(x => (double)(DateTime.Now.Year + longOffsets[0]))
                .Source.Should().Contain("NOW()");
            mapper.GetExpression<Row, float>(x => (float)(DateTime.Now.Year + offsets[0]))
                .Source.Should().Contain("NOW()");
        }

        [Fact]
        public void Runtime_positional_indexes_are_rejected_on_rows_and_captures()
        {
            var captured = new string[32];
            var mapper = new BsonMapper();
            Action array = () => mapper.GetExpression<Row, string>(x => x.Factors[DateTime.Now.Day]);
            Action list = () => mapper.GetExpression<Row, string>(x => ((IList<string>)x.Factors)[DateTime.Now.Day]);
            Action element = () => mapper.GetExpression<Row, string>(x => x.Factors.ElementAt(DateTime.Now.Day));
            Action captureDefault = () => mapper.GetExpression<Row, string>(x => captured.ElementAtOrDefault(DateTime.Now.Day));
            array.Should().Throw<NotSupportedException>();
            list.Should().Throw<NotSupportedException>();
            element.Should().Throw<NotSupportedException>();
            captureDefault.Should().Throw<NotSupportedException>();
            Action rowArray = () => mapper.GetExpression<Row, string>(x => x.Factors[x.Id]);
            Action rowList = () => mapper.GetExpression<Row, string>(x => ((IList<string>)x.Factors)[x.Id]);
            Action rowElement = () => mapper.GetExpression<Row, string>(x => x.Factors.ElementAt(x.Id));
            rowArray.Should().Throw<NotSupportedException>();
            rowList.Should().Throw<NotSupportedException>();
            rowElement.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Closed_scalar_branches_do_not_read_unchosen_elements()
        {
            var mapper = new BsonMapper();
            var selected = new[] { 2 };
            var empty = new int[0];
            var choose = true;
            using var db = new LiteDatabase(":memory:", mapper);
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1 }, new Row { Id = 2 } });
            Expression<Func<Row, bool>> predicate = x => x.Id == (choose ? selected[0] : empty[0]);
            col.Find(predicate).Select(x => x.Id).Should().Equal(2);
            selected[0] = 1;
            col.Find(predicate).Select(x => x.Id).Should().Equal(1);

            string present = "kept";
            var emptyStrings = new string[0];
            mapper.GetExpression<Row, string>(x => present ?? emptyStrings[0])
                .ExecuteScalar(new BsonDocument()).AsString.Should().Be("kept");
            var emptyBooleans = new bool[0];
            var flag = true;
            mapper.GetExpression<Row, bool>(x => flag || emptyBooleans[0])
                .ExecuteScalar(new BsonDocument()).AsBoolean.Should().BeTrue();
            flag = false;
            mapper.GetExpression<Row, bool>(x => flag && emptyBooleans[0])
                .ExecuteScalar(new BsonDocument()).AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Independent_closed_branches_and_ordinary_calls_preserve_runtime_compatibility()
        {
            var mapper = new BsonMapper();
            var selected = new[] { 2 };
            var empty = new int[0];
            var choose = true;
            Expression<Func<Row, bool>> predicate = x => x.Id == (choose ? selected[0] : empty[0]) && DateTime.Now.Year > 2000;
            mapper.GetExpression<Row, bool>(predicate).Source.Should().Contain("NOW()");
            using var db = new LiteDatabase(":memory:", mapper);
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1 }, new Row { Id = 2 } });
            col.Find(predicate).Select(x => x.Id).Should().Equal(2);
            mapper.GetExpression<Row, long>(x => (long)((choose ? selected[0] : empty[0]) + DateTime.Now.Year))
                .Source.Should().Contain("NOW()");
            mapper.GetExpression<Row, int>(x => DateTime.Now.AddDays(choose ? selected[0] : empty[0]).Year)
                .Source.Should().Contain("NOW()");
            // Ordinary non-element calls retain their existing CLR evaluation path.
            mapper.GetExpression<Row, int>(x => DateTime.Now.GetHashCode())
                .ExecuteScalar(new BsonDocument()).IsInt32.Should().BeTrue();
            mapper.GetExpression<Row, int>(x => Guid.NewGuid().GetHashCode())
                .ExecuteScalar(new BsonDocument()).IsInt32.Should().BeTrue();
        }

        [Fact]
        public void Empty_row_keys_are_rejected_without_aliasing_the_parent_path()
        {
            var key = string.Empty;
            var mapper = new BsonMapper();
            Action typed = () => mapper.GetExpression<Row, string>(x => x.Values[key]);
            Action raw = () => mapper.GetExpression<BsonDocument, BsonValue>(x => x[key]);
            typed.Should().Throw<NotSupportedException>().WithMessage("Empty row dictionary keys*");
            raw.Should().Throw<NotSupportedException>().WithMessage("Empty row dictionary keys*");
            var captured = new Dictionary<string, string> { [key] = "value" };
            mapper.GetExpression<Row, string>(x => captured[key]).ExecuteScalar(new BsonDocument()).AsString.Should().Be("value");
        }

        [Theory]
        [InlineData("a'b] OR true")]
        [InlineData("quoted\"key")]
        [InlineData("line\nslash\\key")]
        [InlineData("a.b")]
        public void Row_dictionary_indexes_escape_captured_field_names(string key)
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[]
            {
                new Row { Id = 1, Values = new Dictionary<string, string> { [key] = "wanted" } },
                new Row { Id = 2, Values = new Dictionary<string, string> { [key] = "other" } }
            });
            col.Find(x => x.Values[key] == "wanted").Select(x => x.Id).Should().Equal(1);
            var raw = db.GetCollection("raw");
            raw.Insert(new BsonDocument { ["_id"] = 1, [key] = "wanted" });
            raw.Insert(new BsonDocument { ["_id"] = 2, [key] = "other" });
            raw.Find(x => x[key] == "wanted").Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Fact]
        public void Captured_scalar_list_and_dictionary_values_preserve_row_index_queries()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[]
            {
                new Row { Id = 1, Factors = new[] { "Base" } },
                new Row { Id = 2, Factors = new[] { "Other" } }
            });
            var list = new List<string> { "Base" };
            var dictionary = new Dictionary<string, string> { ["a'b] OR true"] = "Other" };
            Expression<Func<Row, bool>> predicate = x => x.Factors[0] == list[0];
            col.Find(predicate).Select(x => x.Id).Should().Equal(1);
            list[0] = "Other";
            col.Find(predicate).Select(x => x.Id).Should().Equal(2);
            col.Find(x => x.Factors[0] == dictionary["a'b] OR true"]).Select(x => x.Id).Should().Equal(2);
            var matrix = new int[,] { { 1, 2 } };
            col.Find(x => x.Id == matrix[0, 1]).Select(x => x.Id).Should().Equal(2);
        }

        [Fact]
        public void Explicit_IndexExpression_reads_current_captured_value()
        {
            var values = new List<int> { 2 };
            var row = Expression.Parameter(typeof(Row), "row");
            var index = Expression.MakeIndex(Expression.Constant(values), typeof(List<int>).GetProperty("Item"),
                new[] { Expression.Constant(0) });
            var predicate = Expression.Lambda<Func<Row, bool>>(
                Expression.Equal(Expression.Property(row, nameof(Row.Id)), index), row);
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1 }, new Row { Id = 2 } });
            col.Find(predicate).Select(x => x.Id).Should().Equal(2);
            values[0] = 1;
            col.Find(predicate).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        public void Captured_array_index_tracks_mutation_and_selects_exact_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("factors");
            col.Insert(new BsonDocument { ["_id"] = 1, ["Factor"] = "Base" });
            col.Insert(new BsonDocument { ["_id"] = 2, ["Factor"] = "Other" });
            var parameters = new[] { "Base", "Other", "Missing" };
            foreach (var index in new[] { 0, 1, 2, 0 })
            {
                var expected = col.FindAll().Where(x => x["Factor"].AsString == parameters[index])
                    .Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
                col.Find(x => x["Factor"] == parameters[index]).Select(x => x["_id"].AsInt32)
                    .OrderBy(x => x).Should().Equal(expected);
            }
            parameters[0] = "Other";
            col.Find(x => x["Factor"] == parameters[0]).Select(x => x["_id"].AsInt32).Should().Equal(2);
            col.Count().Should().Be(2);
        }
    }
}
