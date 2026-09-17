using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IncludedBooleanSafety_Tests
    {
        [Theory]
        [InlineData("(Ref.Score >= 101 AND (Ref.Score < 102 OR Ref.Score > 103)) OR Ref.Score = 102")]
        [InlineData("Ref.Score IN [101,104] AND (Ref.Score < 102 OR Ref.Score > 103)")]
        [InlineData("(Ref.Score < 103 OR Ref.Score > 104) AND Ref.Score >= 101 AND Ref.Score <= 104")]
        [InlineData("(Ref.Score = 101 AND Score = 0) OR (Ref.Score = 101 AND Score = 8)")]
        public void Affected_boolean_members_keep_the_original_filter(string predicate)
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            var expected = rows.Query().Include("Ref").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            expected.Should().NotBeEmpty();
            rows.EnsureIndex("affected", "Ref.Score");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Fact]
        public void Every_include_must_preserve_the_candidate_path()
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Owner: { $id: Ref.$id, $ref: 'owners', Score: Owner.Score, Manager: Owner.Manager } }", "true");
            const string predicate = "(owner.score >= 101 AND owner.score < 103) OR owner.score = 104";
            var expected = rows.Query().Include("Owner.Manager").Include("Owner").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            expected.Should().NotBeEmpty();
            rows.EnsureIndex("affected", "Owner.Score");
            var query = rows.Query().Include("OWNER.manager").Include("owner").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Theory]
        [InlineData("(Values[*] ANY > 8 AND Values[*] ANY < 3) OR Values[*] ANY = 5")]
        [InlineData("Values[*] ALL < 3 OR Values[*] ALL > 8")]
        public void Separate_array_predicates_keep_their_multikey_semantics(string predicate)
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Values: [1,10] }", "true");
            var expected = rows.Query().Include("Ref").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Theory]
        [InlineData("Score < 100 OR Score > (1 % @zero)", false)]
        [InlineData("Score < 0 OR Score > (1 % @zero)", true)]
        [InlineData("(Score = -1 AND SUBSTRING(Ref.Name,1000) = 'x') OR (Score = -1 AND RANDOM() > 0)", false)]
        [InlineData("(Score = 0 AND SUBSTRING(Ref.Name,1000) = 'x') OR (Score = 0 AND RANDOM() > 0)", true)]
        public void Bound_errors_and_residual_errors_keep_short_circuit_semantics(string predicate, bool throws)
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("key", "Score");
            var query = rows.Query().Include("Ref").Where(predicate, new BsonDocument { ["zero"] = 0 });
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            if (throws)
            {
                Action execute = () => query.ToArray();
                execute.Should().Throw<Exception>();
            }
            else query.ToArray().Length.Should().Be(predicate.StartsWith("Score") ? 40 : 0);
        }

        [Fact]
        public void Unsupported_array_selectors_and_volatile_bounds_keep_their_filters()
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Owners: [Ref] }", "true");
            rows.EnsureIndex("affected", "Owners[0].Score");
            var query = rows.Query().Include("Owners[*]").Where("(Owners[0].Score > 100 AND Owners[0].Score < 103) OR Owners[0].Score = 104");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Length.Should().Be(30);
            rows.EnsureIndex("score", "Score");
            rows.Query().Include("Ref").Where("Score < 2 OR Score > RANDOM()").GetPlan().ContainsKey("filters").Should().BeTrue();
        }
    }
}
