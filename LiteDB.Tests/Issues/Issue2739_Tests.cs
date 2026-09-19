using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2739_Tests
    {
        public class Target
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        public class SpecialTarget : Target
        {
            public string Extra { get; set; }
        }

        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")]
            public Target Reference { get; set; }
            [BsonRef("other-targets")]
            public Target Other { get; set; }
            [BsonField("refs"), BsonRef("targets")]
            public List<Target> References { get; set; }
        }

        [Fact]
        public void Captured_assignments_use_each_destination_mapping_and_reread_values()
        {
            var mapper = new BsonMapper();
            Target captured = new SpecialTarget { Id = 3, Name = "first", Extra = "derived" };
            Expression<Func<Row, Row>> transform = x => new Row { Reference = captured, Other = captured };

            var first = mapper.GetExpression(transform).ExecuteScalar().AsDocument;
            first["Reference"]["$ref"].AsString.Should().Be("targets");
            first["Other"]["$ref"].AsString.Should().Be("other-targets");
            first["Reference"].AsDocument.Keys.Should().BeEquivalentTo("$id", "$ref", "$type");
            first["Reference"]["$type"].Should().Be(mapper.ToDocument(new Row { Reference = captured })["Reference"]["$type"]);

            captured = new Target { Id = 4, Name = "replacement" };
            var second = mapper.GetExpression(transform).ExecuteScalar().AsDocument;
            second["Reference"].AsDocument.Keys.Should().BeEquivalentTo("$id", "$ref");
            second["Reference"]["$id"].AsInt32.Should().Be(4);
            first["Reference"]["$id"].AsInt32.Should().Be(3);

            captured = null;
            mapper.GetExpression(transform).ExecuteScalar()["Reference"].IsNull.Should().BeTrue();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Captured_reference_lists_match_insert_serialization(bool useInitializer)
        {
            var mapper = new BsonMapper();
            var target = new Target { Id = 7, Name = "never inline" };
            var derived = new SpecialTarget { Id = 8, Extra = "never inline" };
            var values = new List<Target> { target, null, derived };
            Expression<Func<Row, Row>> transform = useInitializer
                ? x => new Row { References = new List<Target> { target, null, derived } }
                : x => new Row { References = values };

            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1 });
            rows.UpdateMany(transform, x => x.Id == 1).Should().Be(1);
            var stored = db.GetCollection("rows").FindById(1)["refs"];
            stored.Should().Be(mapper.ToDocument(new Row { References = values })["refs"]);
            stored.AsArray.ToArray().Should().HaveCount(2);
            stored.AsArray.Select(x => x["$id"].AsInt32).Should().Equal(7, 8);

            values.Clear();
            rows.UpdateMany(x => new Row { References = values }, x => x.Id == 1);
            db.GetCollection("rows").FindById(1)["refs"].AsArray.ToArray().Should().BeEmpty();
            rows.UpdateMany(x => new Row { References = null }, x => x.Id == 1);
            db.GetCollection("rows").FindById(1)["refs"].IsNull.Should().BeTrue();
        }

        [Fact]
        public void Captured_references_can_mix_with_expression_only_reference_markers()
        {
            var target = new Target { Id = 9, Name = "never inline" };
            var mapper = new BsonMapper();
            var expr = mapper.GetExpression<Row, Row>(x => new Row
            {
                References = new List<Target> { null, target, null, new BsonRefId<Target>(x.Id + 10), null }
            });
            var values = expr.ExecuteScalar(new BsonDocument { ["_id"] = 1 })["refs"].AsArray;
            values.ToArray().Should().HaveCount(2);
            values.Select(x => x["$id"].AsInt32).Should().Equal(9, 11);
            values.ToArray().Should().OnlyContain(x => x.AsDocument.Count == 2 && x["$ref"] == "targets");
        }

        [Fact]
        public void Existing_row_references_keep_their_stored_shape()
        {
            var mapper = new BsonMapper();
            var source = mapper.ToDocument(new Row { Id = 1, Reference = new Target { Id = 5 } });
            var expr = mapper.GetExpression<Row, Row>(x => new Row { Reference = x.Reference });
            expr.ExecuteScalar(source)["Reference"].Should().Be(source["Reference"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Row_dependent_branches_keep_destination_reference_serialization(bool coalesce)
        {
            var mapper = new BsonMapper();
            var captured = new Target { Id = 9, Name = "never inline" };
            var captures = new List<Target> { captured, null };
            Expression<Func<Row, Row>> transform = coalesce
                ? x => new Row { Reference = x.Reference ?? captured, References = x.References ?? captures }
                : x => new Row
                {
                    Reference = x.Id == 1 ? captured : x.Reference,
                    References = x.Id == 1 ? captures : x.References
                };
            var expression = mapper.GetExpression(transform);
            var source = mapper.ToDocument(new Row { Id = 1 });
            var bound = expression.ExecuteScalar(source).AsDocument;
            bound["Reference"].AsDocument.Keys.Should().BeEquivalentTo("$id", "$ref");
            bound["Reference"]["$id"].AsInt32.Should().Be(9);
            bound["refs"].AsArray.Count.Should().Be(1);
            bound["refs"][0]["$id"].AsInt32.Should().Be(9);

            source = mapper.ToDocument(new Row
            {
                Id = 2, Reference = new Target { Id = 7 }, References = new List<Target> { new Target { Id = 8 } }
            });
            var preserved = expression.ExecuteScalar(source);
            preserved["Reference"].Should().Be(source["Reference"]);
            preserved["refs"].Should().Be(source["refs"]);
        }

        [Fact]
        public void UpdateMany_stores_a_reference_and_later_includes_current_target_data()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var oldTarget = new Target { Id = 1, Name = "old" };
                var newTarget = new Target { Id = 2, Name = "new" };
                var targets = db.GetCollection<Target>("targets");
                targets.Insert(new[] { oldTarget, newTarget });
                var rows = db.GetCollection<Row>("rows");
                rows.Insert(new[] { new Row { Id = 11, Reference = oldTarget }, new Row { Id = 12, Reference = oldTarget } });
                rows.UpdateMany(x => new Row { Id = x.Id, Reference = newTarget }, x => x.Id == 11).Should().Be(1);
                newTarget.Name = "changed after assignment";
                targets.Update(newTarget).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var raw = db.GetCollection("rows");
                var reference = raw.FindById(11)["Reference"].AsDocument;
                reference.Keys.Should().BeEquivalentTo("$id", "$ref");
                reference["$id"].AsInt32.Should().Be(2);
                reference["$ref"].AsString.Should().Be("targets");
                var rows = db.GetCollection<Row>("rows").Include(x => x.Reference);
                rows.FindById(11).Reference.Name.Should().Be("changed after assignment");
                rows.FindById(12).Reference.Name.Should().Be("old");
                raw.Count().Should().Be(2);
            }
        }
    }
}
